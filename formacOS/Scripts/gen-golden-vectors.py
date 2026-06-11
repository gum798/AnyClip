#!/usr/bin/env python3
"""Generate wire-protocol golden vectors using the exact encoding rules of
anyclip.py (_send): json.dumps(ensure_ascii=False).encode("utf-8") behind a
4-byte big-endian length prefix. Stdlib only. Re-run when the protocol
changes; fixtures are committed.
"""
import base64
import hashlib
import json
import pathlib

OUT = pathlib.Path(__file__).resolve().parent.parent / "Tests" / "AnyClipCoreTests" / "Fixtures"

TOKEN = "golden-token"
TOKEN_HASH = hashlib.sha256(TOKEN.encode("utf-8")).hexdigest()
NODE_ID = "11111111-2222-3333-4444-555555555555"
TEXT = "안녕 AnyClip 👋 line1\nline2"
IMAGE_BYTES = b"\x89PNG\r\n\x1a\n" + bytes(range(64))
FILE_NAME = "réport final.txt"
FILE_BYTES = b"golden file body \x00\x01\x02"
TS = 1718000000.5


def frame(obj: dict) -> bytes:
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    return len(data).to_bytes(4, "big") + data


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    vectors = {
        "hello.bin": {
            "type": "hello", "token": TOKEN_HASH, "node_id": NODE_ID,
            "name": "golden-mac", "version": 1, "app_version": "1.0.0",
            "protocol_major": 1, "protocol_minor": 0,
        },
        "clip_text.bin": {
            "type": "clip", "kind": "text", "content": TEXT,
            "hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(), "ts": TS,
        },
        "clip_image.bin": {
            "type": "clip", "kind": "image",
            "content": base64.b64encode(IMAGE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(IMAGE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(IMAGE_BYTES),
        },
        "clip_file.bin": {
            "type": "clip", "kind": "file", "name": FILE_NAME,
            "content": base64.b64encode(FILE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(FILE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(FILE_BYTES),
        },
        "ping.bin": {"type": "ping", "ts": TS},
    }
    for fname, obj in vectors.items():
        (OUT / fname).write_bytes(frame(obj))
    manifest = {
        "token": TOKEN, "token_hash": TOKEN_HASH, "node_id": NODE_ID,
        "text": TEXT,
        "text_hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(),
        "image_b64": base64.b64encode(IMAGE_BYTES).decode("ascii"),
        "image_hash": hashlib.sha256(IMAGE_BYTES).hexdigest(),
        "file_name": FILE_NAME,
        "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
        "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
        "ts": TS,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {len(vectors) + 1} fixtures to {OUT}")


if __name__ == "__main__":
    main()
