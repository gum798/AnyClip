#!/usr/bin/env python3
"""AnyClip - simple cross-device clipboard sharing over LAN.

Phase 1 scope: Windows <-> macOS, text only, mDNS auto-discovery, shared token.
"""
from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import logging
import os
import socket
import sys
import time
import uuid
from dataclasses import dataclass
from typing import Optional

import pyperclip
from zeroconf import IPVersion, ServiceInfo, ServiceStateChange
from zeroconf.asyncio import AsyncServiceBrowser, AsyncZeroconf

SERVICE_TYPE = "_anyclip._tcp.local."
PROTOCOL_VERSION = 1
MAX_PAYLOAD = 4 * 1024 * 1024  # 4 MiB hard cap per frame
DEFAULT_PORT = 24816
HANDSHAKE_TIMEOUT = 5.0
CONNECT_TIMEOUT = 5.0

log = logging.getLogger("anyclip")


def sha256_hex(data: str) -> str:
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


@dataclass
class Config:
    token: str
    port: int
    name: str
    poll_interval: float
    verbose: bool


def parse_args() -> Config:
    parser = argparse.ArgumentParser(
        prog="anyclip",
        description="AnyClip - cross-device clipboard sync over LAN (text only).",
    )
    env_token = os.environ.get("ANYCLIP_TOKEN")
    parser.add_argument(
        "--token",
        default=env_token,
        help="Shared secret. Both peers must use the same value. "
             "Falls back to ANYCLIP_TOKEN env var.",
    )
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"TCP port to listen on (default: {DEFAULT_PORT})")
    parser.add_argument("--name", default=socket.gethostname(),
                        help="Display name for this node (default: hostname)")
    parser.add_argument("--poll", type=float, default=0.5,
                        help="Clipboard poll interval in seconds (default: 0.5)")
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="Enable DEBUG logging")
    args = parser.parse_args()
    if not args.token:
        sys.stderr.write(
            "error: --token is required (or set ANYCLIP_TOKEN env var)\n"
        )
        sys.exit(2)
    return Config(
        token=args.token,
        port=args.port,
        name=args.name,
        poll_interval=max(0.1, args.poll),
        verbose=args.verbose,
    )


class EchoSuppressor:
    """Tracks the hash of the last text received from a peer.

    The clipboard poller consults this before sending so we don't
    bounce a peer's update right back at them.
    """

    def __init__(self) -> None:
        self.last_received_hash: Optional[str] = None

    def mark_received(self, text: str) -> None:
        self.last_received_hash = sha256_hex(text)

    def should_send(self, text: str) -> bool:
        return sha256_hex(text) != self.last_received_hash


class ClipboardWatcher:
    def __init__(self, poll_interval: float, on_change) -> None:
        self.poll_interval = poll_interval
        self.on_change = on_change
        self._last: Optional[str] = self._safe_paste()

    @staticmethod
    def _safe_paste() -> Optional[str]:
        try:
            return pyperclip.paste()
        except Exception as exc:
            log.debug(f"clipboard read failed: {exc}")
            return None

    async def run(self) -> None:
        while True:
            text = self._safe_paste()
            if text is not None and text != self._last:
                self._last = text
                try:
                    await self.on_change(text)
                except Exception as exc:
                    log.exception(f"on_change handler failed: {exc}")
            await asyncio.sleep(self.poll_interval)

    def update_local(self, text: str) -> None:
        """Set the local clipboard without re-triggering on_change."""
        self._last = text
        try:
            pyperclip.copy(text)
        except Exception as exc:
            log.warning(f"clipboard write failed: {exc}")


class PeerLink:
    """Owns the single active TCP link to a peer.

    Acts as both server and client; resolves the simultaneous-connect
    race via lexicographic node_id tie-break.
    """

    def __init__(self, config: Config, node_id: str, on_clip) -> None:
        self.config = config
        self.node_id = node_id
        self.on_clip = on_clip
        self._writer: Optional[asyncio.StreamWriter] = None
        self._peer_node_id: Optional[str] = None
        self._lock = asyncio.Lock()
        self._token_hash = sha256_hex(config.token)

    @property
    def active(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    async def serve(self) -> None:
        server = await asyncio.start_server(
            self._handle_inbound, host="0.0.0.0", port=self.config.port,
        )
        log.info(f"listening on tcp/{self.config.port}")
        async with server:
            await server.serve_forever()

    async def _handle_inbound(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
    ) -> None:
        peer = writer.get_extra_info("peername")
        log.debug(f"inbound from {peer}")
        try:
            await self._session(reader, writer, inbound=True)
        except Exception as exc:
            log.debug(f"inbound session ended: {exc}")
        finally:
            self._safe_close(writer)

    async def try_connect(self, host: str, port: int) -> None:
        if self.active:
            return
        try:
            reader, writer = await asyncio.wait_for(
                asyncio.open_connection(host, port), timeout=CONNECT_TIMEOUT,
            )
        except Exception as exc:
            log.debug(f"connect to {host}:{port} failed: {exc}")
            return
        log.debug(f"outbound connected to {host}:{port}")
        try:
            await self._session(reader, writer, inbound=False)
        except Exception as exc:
            log.debug(f"outbound session ended: {exc}")
        finally:
            self._safe_close(writer)

    @staticmethod
    def _safe_close(writer: asyncio.StreamWriter) -> None:
        try:
            writer.close()
        except Exception:
            pass

    async def _session(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        inbound: bool,
    ) -> None:
        await self._send(writer, {
            "type": "hello",
            "token": self._token_hash,
            "node_id": self.node_id,
            "name": self.config.name,
            "version": PROTOCOL_VERSION,
        })
        try:
            hello = await asyncio.wait_for(self._recv(reader), timeout=HANDSHAKE_TIMEOUT)
        except asyncio.TimeoutError:
            log.warning("handshake timeout")
            return
        if not hello or hello.get("type") != "hello":
            log.warning("invalid hello, closing")
            return
        if hello.get("token") != self._token_hash:
            log.warning(f"auth failed from peer name={hello.get('name')!r}")
            return
        if hello.get("version") != PROTOCOL_VERSION:
            log.warning(f"version mismatch: peer={hello.get('version')}")
            return
        peer_id = hello.get("node_id")
        if not isinstance(peer_id, str) or peer_id == self.node_id:
            log.debug("self loopback or bad node_id, dropping")
            return

        async with self._lock:
            if self.active:
                # Both sides connected concurrently. Keep the link where the
                # smaller node_id is the *outbound* (caller) side.
                keep_this_outbound = (not inbound) and self.node_id < peer_id
                if keep_this_outbound:
                    log.debug("tie-breaker: replacing existing link")
                    self._safe_close(self._writer)  # type: ignore[arg-type]
                else:
                    log.debug("tie-breaker: dropping duplicate link")
                    return
            self._writer = writer
            self._peer_node_id = peer_id

        log.info(
            f"linked with peer name={hello.get('name')!r} "
            f"id={peer_id[:8]} ({'inbound' if inbound else 'outbound'})"
        )

        try:
            while True:
                msg = await self._recv(reader)
                if msg is None:
                    break
                if msg.get("type") == "clip":
                    content = msg.get("content")
                    if isinstance(content, str):
                        await self.on_clip(content)
                else:
                    log.debug(f"ignoring message type: {msg.get('type')}")
        finally:
            async with self._lock:
                if self._writer is writer:
                    self._writer = None
                    self._peer_node_id = None
            log.info("peer disconnected")

    async def _send(self, writer: asyncio.StreamWriter, obj: dict) -> None:
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        if len(data) > MAX_PAYLOAD:
            log.warning(f"payload too large ({len(data)} bytes), dropping")
            return
        try:
            writer.write(len(data).to_bytes(4, "big"))
            writer.write(data)
            await writer.drain()
        except Exception as exc:
            log.debug(f"send failed: {exc}")

    async def _recv(self, reader: asyncio.StreamReader) -> Optional[dict]:
        try:
            head = await reader.readexactly(4)
        except asyncio.IncompleteReadError:
            return None
        n = int.from_bytes(head, "big")
        if n == 0 or n > MAX_PAYLOAD:
            log.warning(f"invalid frame length: {n}")
            return None
        try:
            body = await reader.readexactly(n)
        except asyncio.IncompleteReadError:
            return None
        try:
            return json.loads(body.decode("utf-8"))
        except Exception as exc:
            log.warning(f"bad json: {exc}")
            return None

    async def send_clip(self, text: str) -> None:
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        await self._send(writer, {
            "type": "clip",
            "content": text,
            "hash": sha256_hex(text),
            "ts": time.time(),
        })


class MdnsBeacon:
    """Advertises this node and browses for peers on the LAN."""

    def __init__(self, config: Config, node_id: str, on_peer) -> None:
        self.config = config
        self.node_id = node_id
        self.on_peer = on_peer
        self._azc: Optional[AsyncZeroconf] = None
        self._browser: Optional[AsyncServiceBrowser] = None
        self._info: Optional[ServiceInfo] = None
        self._loop: Optional[asyncio.AbstractEventLoop] = None

    async def start(self) -> None:
        self._loop = asyncio.get_running_loop()
        self._azc = AsyncZeroconf(ip_version=IPVersion.V4Only)

        instance = f"{self.config.name}-{self.node_id[:8]}.{SERVICE_TYPE}"
        self._info = ServiceInfo(
            type_=SERVICE_TYPE,
            name=instance,
            port=self.config.port,
            properties={
                "id": self.node_id,
                "version": str(PROTOCOL_VERSION),
            },
        )
        await self._azc.async_register_service(self._info)
        log.info(f"mDNS advertised as {instance!r}")

        self._browser = AsyncServiceBrowser(
            self._azc.zeroconf, [SERVICE_TYPE], handlers=[self._handler],
        )

    def _handler(self, zeroconf, service_type, name, state_change) -> None:
        # zeroconf invokes this from its own thread; bounce onto our loop.
        if state_change != ServiceStateChange.Added:
            return
        loop = self._loop
        if loop is None:
            return
        asyncio.run_coroutine_threadsafe(self._resolve(name), loop)

    async def _resolve(self, name: str) -> None:
        try:
            assert self._azc is not None
            info = await self._azc.async_get_service_info(SERVICE_TYPE, name, timeout=3000)
            if info is None:
                return
            props = {}
            for k, v in (info.properties or {}).items():
                key = k.decode() if isinstance(k, (bytes, bytearray)) else k
                val = v.decode() if isinstance(v, (bytes, bytearray)) else v
                props[key] = val
            peer_id = props.get("id")
            if peer_id == self.node_id:
                return  # ourselves
            addrs = info.parsed_addresses()
            if not addrs or not info.port:
                return
            host = addrs[0]
            port = info.port
            log.info(f"discovered peer {name!r} at {host}:{port}")
            await self.on_peer(host, port)
        except Exception as exc:
            log.debug(f"resolve failed: {exc}")

    async def stop(self) -> None:
        try:
            if self._browser is not None:
                await self._browser.async_cancel()
        except Exception:
            pass
        try:
            if self._info is not None and self._azc is not None:
                await self._azc.async_unregister_service(self._info)
        except Exception:
            pass
        try:
            if self._azc is not None:
                await self._azc.async_close()
        except Exception:
            pass


async def run(config: Config) -> None:
    logging.basicConfig(
        level=logging.DEBUG if config.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(message)s",
    )
    node_id = str(uuid.uuid4())
    suppressor = EchoSuppressor()

    # Forward declaration for the closure below.
    watcher: ClipboardWatcher

    async def on_remote_clip(text: str) -> None:
        suppressor.mark_received(text)
        watcher.update_local(text)
        log.info(f"<- received {len(text)} chars")

    link = PeerLink(config, node_id, on_remote_clip)

    async def on_local_change(text: str) -> None:
        if not link.active:
            return
        if not suppressor.should_send(text):
            log.debug("skip echo of just-received clip")
            return
        await link.send_clip(text)
        log.info(f"-> sent {len(text)} chars")

    watcher = ClipboardWatcher(config.poll_interval, on_local_change)
    beacon = MdnsBeacon(config, node_id, link.try_connect)

    log.info(f"AnyClip starting (node {node_id[:8]}, name={config.name!r})")
    try:
        await beacon.start()
        await asyncio.gather(link.serve(), watcher.run())
    finally:
        await beacon.stop()


def main() -> None:
    config = parse_args()
    try:
        asyncio.run(run(config))
    except KeyboardInterrupt:
        sys.stderr.write("\nshutting down\n")


if __name__ == "__main__":
    main()
