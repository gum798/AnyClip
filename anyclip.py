#!/usr/bin/env python3
"""AnyClip - simple cross-device clipboard sharing over LAN.

Phase 1 scope: Windows <-> macOS, text only, mDNS auto-discovery, shared token.
"""
from __future__ import annotations

import argparse
import asyncio
import errno
import hashlib
import json
import logging
import os
import signal
import socket
import subprocess
import sys
import time
import uuid
from dataclasses import dataclass
from logging.handlers import RotatingFileHandler
from pathlib import Path
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

LOG_DIR = Path.home() / ".anyclip"
LOG_FILE = LOG_DIR / "anyclip.log"
LOG_MAX_BYTES = 5 * 1024 * 1024
LOG_BACKUP_COUNT = 3
PID_FILE = LOG_DIR / "anyclip.pid"


class FatalStartupError(RuntimeError):
    """Raised when the daemon cannot start and retrying will not help.

    The supervisor in main() recognises this and exits with the message
    instead of looping with backoff.
    """


def _process_alive(pid: int) -> bool:
    """True if the OS reports a process with this pid (best-effort)."""
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        # Process exists but is owned by another user.
        return True
    except OSError:
        return False


def _is_anyclip_pid(pid: int) -> bool:
    """Best-effort: does this pid look like an anyclip.py process?

    Used as a guard before we kill anything: we never terminate a process
    that is not clearly one of our own daemons. On Windows we cannot probe
    this cheaply without psutil, so we trust the PID-file evidence alone.
    """
    if sys.platform == "win32":
        return True
    try:
        out = subprocess.check_output(
            ["ps", "-p", str(pid), "-o", "args="],
            stderr=subprocess.DEVNULL, text=True, timeout=2,
        )
    except (subprocess.SubprocessError, OSError):
        return False
    return "anyclip" in out


def _find_listening_pid(port: int) -> Optional[int]:
    """Best-effort: pid LISTENing on tcp `port`. None on Windows / not found."""
    if sys.platform == "win32":
        return None
    try:
        out = subprocess.check_output(
            ["lsof", "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"],
            stderr=subprocess.DEVNULL, text=True, timeout=2,
        )
    except (subprocess.SubprocessError, OSError):
        return None
    for line in out.splitlines():
        line = line.strip()
        if line.isdigit():
            return int(line)
    return None


def _terminate_pid(pid: int) -> bool:
    """SIGTERM, wait up to 2s, then SIGKILL on POSIX. Returns True if pid is gone."""
    try:
        os.kill(pid, signal.SIGTERM)
    except ProcessLookupError:
        return True
    except OSError:
        return not _process_alive(pid)
    for _ in range(20):
        time.sleep(0.1)
        if not _process_alive(pid):
            return True
    if sys.platform != "win32":
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            pass
        for _ in range(10):
            time.sleep(0.1)
            if not _process_alive(pid):
                return True
    return not _process_alive(pid)


def prepare_pid_lock(port: int) -> None:
    """Ensure we are the only anyclip.

    If a previous anyclip is detected (via PID file or via ``lsof`` on the
    listening port) we terminate it and continue. Foreign processes that
    happen to hold the port are NEVER killed -- we raise FatalStartupError
    so the user can decide.
    """
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    # 1) PID file from a previous run.
    if PID_FILE.exists():
        old_pid = 0
        try:
            content = PID_FILE.read_text().strip().split()
            if content:
                old_pid = int(content[0])
        except (OSError, ValueError):
            old_pid = 0
        if old_pid and old_pid != os.getpid() and _process_alive(old_pid):
            log.info(f"another anyclip detected (pid {old_pid} via PID file); terminating")
            if not _terminate_pid(old_pid):
                raise FatalStartupError(
                    f"could not terminate previous anyclip (pid {old_pid}); "
                    f"please run: kill -9 {old_pid}"
                )
            log.info(f"previous anyclip (pid {old_pid}) terminated")

    # 2) Stale state: PID file missing or already-cleared, but the port is held.
    listener_pid = _find_listening_pid(port)
    if listener_pid and listener_pid != os.getpid():
        if _is_anyclip_pid(listener_pid):
            log.info(
                f"anyclip listening on tcp/{port} (pid {listener_pid}); terminating"
            )
            if not _terminate_pid(listener_pid):
                raise FatalStartupError(
                    f"could not terminate anyclip on tcp/{port} (pid {listener_pid}); "
                    f"please run: kill -9 {listener_pid}"
                )
            # Give the OS a moment to release the socket so our bind() succeeds.
            time.sleep(0.3)
        else:
            raise FatalStartupError(
                f"tcp/{port} is held by a non-anyclip process (pid {listener_pid}); "
                f"stop that process or pick a different --port"
            )

    # 3) Record our pid (and chosen port for diagnostics).
    try:
        PID_FILE.write_text(f"{os.getpid()} {port}\n")
    except OSError as exc:
        log.warning(f"could not write PID file {PID_FILE}: {exc}")


def release_pid_lock() -> None:
    """Remove our PID file, but only if it still points at us."""
    try:
        if not PID_FILE.exists():
            return
        content = PID_FILE.read_text().strip().split()
        if content and int(content[0]) == os.getpid():
            PID_FILE.unlink()
    except (OSError, ValueError):
        pass


def setup_logging(verbose: bool) -> None:
    """Configure root logger with a rotating file handler + console handler.

    Idempotent: removes existing handlers first so supervisor restarts do
    not stack duplicates. File handler is always DEBUG; console respects
    --verbose.
    """
    fmt = "%(asctime)s %(levelname)s %(message)s"
    formatter = logging.Formatter(fmt)
    root = logging.getLogger()
    for handler in list(root.handlers):
        root.removeHandler(handler)
    root.setLevel(logging.DEBUG)

    console = logging.StreamHandler(sys.stderr)
    console.setLevel(logging.DEBUG if verbose else logging.INFO)
    console.setFormatter(formatter)
    root.addHandler(console)

    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        file_handler = RotatingFileHandler(
            str(LOG_FILE),
            maxBytes=LOG_MAX_BYTES,
            backupCount=LOG_BACKUP_COUNT,
            encoding="utf-8",
        )
        file_handler.setLevel(logging.DEBUG)
        file_handler.setFormatter(formatter)
        root.addHandler(file_handler)
    except OSError as exc:
        log.warning(f"file logging disabled: {exc}")


def sha256_hex(data: str) -> str:
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


def parse_peer_arg(value: str) -> tuple[str, int]:
    """Parse a --peer argument: 'host' or 'host:port'."""
    if ":" in value:
        host, _, port_s = value.rpartition(":")
        try:
            port = int(port_s)
        except ValueError:
            raise argparse.ArgumentTypeError(f"invalid port in --peer {value!r}")
    else:
        host = value
        port = DEFAULT_PORT
    host = host.strip()
    if not host:
        raise argparse.ArgumentTypeError(f"empty host in --peer {value!r}")
    return (host, port)


def get_local_ipv4() -> Optional[str]:
    """Best-effort primary IPv4 of this host (the source IP for the default route)."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.connect(("8.8.8.8", 80))
        return sock.getsockname()[0]
    except OSError:
        return None
    finally:
        sock.close()


@dataclass
class Config:
    token: str
    port: int
    name: str
    poll_interval: float
    verbose: bool
    peers: list  # list[tuple[str, int]]; manual fallback peers


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
    parser.add_argument("--peer", type=parse_peer_arg, action="append", default=[],
                        metavar="HOST[:PORT]",
                        help="Manual peer fallback. Repeatable; coexists with mDNS. "
                             "Useful when mDNS is blocked (e.g. corporate Wi-Fi). "
                             f"Default port: {DEFAULT_PORT}.")
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="Enable DEBUG logging on the console (file log is always DEBUG)")
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
        peers=list(args.peer or []),
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
    READ_FAIL_WARN_AT = 5

    def __init__(self, poll_interval: float, on_change) -> None:
        self.poll_interval = poll_interval
        self.on_change = on_change
        self._consec_read_fails = 0
        self._read_fail_warned = False
        self._last: Optional[str] = self._safe_paste()

    def _safe_paste(self) -> Optional[str]:
        try:
            text = pyperclip.paste()
        except Exception as exc:
            self._consec_read_fails += 1
            log.debug(f"clipboard read failed (#{self._consec_read_fails}): {exc}")
            if (self._consec_read_fails >= self.READ_FAIL_WARN_AT
                    and not self._read_fail_warned):
                log.warning(
                    f"clipboard read failing: {self._consec_read_fails} consecutive errors "
                    f"(check OS clipboard permissions / pyperclip backend)"
                )
                self._read_fail_warned = True
            return None
        if self._consec_read_fails:
            self._consec_read_fails = 0
            self._read_fail_warned = False
        return text

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


class AuthGate:
    """Per-IP cooldown after repeated handshake failures.

    After ``MAX_FAILS`` failed handshakes from the same IP, that IP is
    blocked for ``COOLDOWN`` seconds. A successful handshake clears the
    counter. Stale entries (older than COOLDOWN) are swept lazily on
    each check so the dict cannot grow unbounded.
    """

    MAX_FAILS = 5
    COOLDOWN = 60.0

    def __init__(self) -> None:
        self._fails: dict = {}  # ip -> (count, last_ts)
        self._lock = asyncio.Lock()

    async def is_blocked(self, ip: str) -> bool:
        async with self._lock:
            self._sweep_locked()
            entry = self._fails.get(ip)
            if entry is None:
                return False
            count, last = entry
            return count >= self.MAX_FAILS and (time.time() - last) < self.COOLDOWN

    async def record_fail(self, ip: str) -> None:
        async with self._lock:
            count, _ = self._fails.get(ip, (0, 0.0))
            self._fails[ip] = (count + 1, time.time())

    async def record_ok(self, ip: str) -> None:
        async with self._lock:
            self._fails.pop(ip, None)

    def _sweep_locked(self) -> None:
        now = time.time()
        stale = [ip for ip, (_, last) in self._fails.items()
                 if now - last >= self.COOLDOWN]
        for ip in stale:
            self._fails.pop(ip, None)


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
        self._auth_gate = AuthGate()

    @property
    def active(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    async def serve(self) -> None:
        try:
            server = await asyncio.start_server(
                self._handle_inbound, host="0.0.0.0", port=self.config.port,
            )
        except OSError as exc:
            if exc.errno == errno.EADDRINUSE:
                raise FatalStartupError(
                    f"port {self.config.port} still in use after cleanup attempt; "
                    f"another process may have grabbed it -- try again or pick --port"
                ) from exc
            raise
        log.info(f"listening on tcp/{self.config.port}")
        async with server:
            await server.serve_forever()

    async def _handle_inbound(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
    ) -> None:
        peer = writer.get_extra_info("peername")
        log.debug(f"inbound from {peer}")
        peer_ip = peer[0] if peer else None
        if peer_ip and await self._auth_gate.is_blocked(peer_ip):
            log.info(
                f"auth gate: {peer_ip} blocked "
                f"(>{AuthGate.MAX_FAILS} failures, cooldown {AuthGate.COOLDOWN:.0f}s)"
            )
            self._safe_close(writer)
            return
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
            log.info(f"connect to {host}:{port} failed: {exc}")
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
        peer_ip = None
        if inbound:
            peer = writer.get_extra_info("peername")
            peer_ip = peer[0] if peer else None
        if hello.get("token") != self._token_hash:
            log.warning(f"auth failed from peer name={hello.get('name')!r}")
            if peer_ip:
                await self._auth_gate.record_fail(peer_ip)
            return
        if hello.get("version") != PROTOCOL_VERSION:
            log.warning(f"version mismatch: peer={hello.get('version')}")
            return
        peer_id = hello.get("node_id")
        if not isinstance(peer_id, str) or peer_id == self.node_id:
            log.debug("self loopback or bad node_id, dropping")
            return
        if peer_ip:
            await self._auth_gate.record_ok(peer_ip)

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
            log.info(f"send failed (link likely down): {exc}")

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

    async def close(self) -> None:
        """Drop the active link if any. Safe to call multiple times."""
        async with self._lock:
            if self._writer is not None:
                self._safe_close(self._writer)
                self._writer = None
                self._peer_node_id = None

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
        local_ip = get_local_ipv4()
        addresses = [socket.inet_aton(local_ip)] if local_ip else []
        server_host = f"anyclip-{self.node_id[:8]}.local."
        self._info = ServiceInfo(
            type_=SERVICE_TYPE,
            name=instance,
            port=self.config.port,
            addresses=addresses,
            server=server_host,
            properties={
                "id": self.node_id,
                "version": str(PROTOCOL_VERSION),
            },
        )
        await self._azc.async_register_service(self._info)
        log.info(f"mDNS advertised as {instance!r} ip={local_ip} server={server_host}")

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
            log.warning(f"peer resolve failed for {name!r}: {exc}")

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


async def peer_keepalive(host: str, port: int, link: "PeerLink") -> None:
    """Maintain an outbound connection to a manually-configured peer.

    Coexists with mDNS-driven discovery: if the link is already active
    (from either source), this just polls until it drops, then retries
    with exponential backoff (1s -> 60s cap). Backoff resets after a
    session that lasted >5s (treated as 'real' uptime).
    """
    backoff = 1.0
    while True:
        if link.active:
            backoff = 1.0
            await asyncio.sleep(2)
            continue
        start = time.monotonic()
        await link.try_connect(host, port)
        elapsed = time.monotonic() - start
        if elapsed > 5.0:
            backoff = 1.0
            continue
        await asyncio.sleep(min(backoff, 60))
        backoff = min(backoff * 2, 60)


async def run(config: Config) -> None:
    setup_logging(config.verbose)
    prepare_pid_lock(config.port)
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
    if config.peers:
        log.info(f"manual peers: {[f'{h}:{p}' for h, p in config.peers]}")
    try:
        await beacon.start()
        tasks = [link.serve(), watcher.run()]
        for host, port in config.peers:
            tasks.append(peer_keepalive(host, port, link))
        await asyncio.gather(*tasks)
    finally:
        await link.close()
        await beacon.stop()
        release_pid_lock()


def main() -> None:
    config = parse_args()
    backoff = 1.0
    while True:
        try:
            asyncio.run(run(config))
            return
        except KeyboardInterrupt:
            sys.stderr.write("\nshutting down\n")
            return
        except SystemExit:
            raise
        except FatalStartupError as exc:
            sys.stderr.write(f"\nanyclip: {exc}\n")
            sys.exit(1)
        except Exception:
            log.exception(f"daemon crashed; restarting in {backoff:.0f}s")
            time.sleep(backoff)
            backoff = min(backoff * 2, 60)


if __name__ == "__main__":
    main()
