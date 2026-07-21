#!/usr/bin/env python3
"""Wire-compatible fake AnyClip peer for interop tests. Stdlib only.

Implements the exact frame + handshake rules of anyclip.py (PeerLink._send/
_recv/_session): 4-byte big-endian length + UTF-8 JSON; hello carries the
sha256-hex token. Listens on 127.0.0.1:<port>, accepts ONE connection,
handshakes, then:

  1. sends one text clip ("hello-from-python"),
  2. appends every received frame as a JSON line to --out,
  3. answers ping with pong,
  4. exits when the connection closes.

Prints READY on stdout once listening.
"""
import argparse
import hashlib
import json
import socket
import struct
import sys
import time
import uuid


def send_frame(conn: socket.socket, obj: dict) -> None:
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    conn.sendall(struct.pack(">I", len(data)) + data)


def recv_exactly(conn: socket.socket, n: int):
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def recv_frame(conn: socket.socket):
    head = recv_exactly(conn, 4)
    if head is None:
        return None
    (n,) = struct.unpack(">I", head)
    if n == 0 or n > 16 * 1024 * 1024:
        return None
    body = recv_exactly(conn, n)
    if body is None:
        return None
    return json.loads(body.decode("utf-8"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--token", required=True)
    ap.add_argument("--out", required=True)
    ap.add_argument("--send-files", action="store_true",
                    help="after handshake, send one kind:'files' clip (2 entries)")
    args = ap.parse_args()

    token_hash = hashlib.sha256(args.token.encode("utf-8")).hexdigest()
    node_id = str(uuid.uuid4())
    out = open(args.out, "w", encoding="utf-8")

    def record(event: str, payload) -> None:
        out.write(json.dumps({"event": event, "data": payload},
                             ensure_ascii=False) + "\n")
        out.flush()

    srv = socket.create_server(("127.0.0.1", args.port))
    sys.stdout.write("READY\n")
    sys.stdout.flush()
    conn, _addr = srv.accept()

    hello = recv_frame(conn)
    record("hello", hello)
    send_frame(conn, {
        "type": "hello", "token": token_hash, "node_id": node_id,
        "name": "fake-peer", "version": 1, "app_version": "9.9.9-test",
        "protocol_major": 1, "protocol_minor": 0,
    })
    if (not hello or hello.get("type") != "hello"
            or hello.get("token") != token_hash):
        record("auth_failed", None)
        conn.close()
        return

    text = "hello-from-python"
    send_frame(conn, {
        "type": "clip", "kind": "text", "content": text,
        "hash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
        "ts": time.time(),
    })

    if args.send_files:
        import base64 as _b64
        import unicodedata as _ud
        entries = [
            (_ud.normalize("NFC", "노트.txt"), b"multi body one"),
            (_ud.normalize("NFC", "(E&S) plan.txt"), b"multi body two"),
        ]
        files_field = [
            {
                "name": n,
                "content": _b64.b64encode(b).decode("ascii"),
                "hash": hashlib.sha256(b).hexdigest(),
                "bytes": len(b),
            }
            for n, b in entries
        ]
        agg = hashlib.sha256(
            "".join(sorted(f["hash"] for f in files_field)).encode("ascii")
        ).hexdigest()
        send_frame(conn, {
            "type": "clip", "kind": "files", "files": files_field,
            "hash": agg, "ts": time.time(),
            "bytes": sum(f["bytes"] for f in files_field),
        })

    while True:
        msg = recv_frame(conn)
        if msg is None:
            break
        if msg.get("type") == "ping":
            send_frame(conn, {"type": "pong", "ts": time.time()})
        clipped = dict(msg)
        content = clipped.get("content")
        if isinstance(content, str) and len(content) > 300:
            clipped["content"] = f"<{len(content)} chars>"
        record("recv", clipped)
    record("closed", None)


if __name__ == "__main__":
    main()
