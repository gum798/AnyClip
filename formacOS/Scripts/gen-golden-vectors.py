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
import unicodedata

OUT = pathlib.Path(__file__).resolve().parent.parent / "Tests" / "AnyClipCoreTests" / "Fixtures"

TOKEN = "golden-token"
TOKEN_HASH = hashlib.sha256(TOKEN.encode("utf-8")).hexdigest()
NODE_ID = "11111111-2222-3333-4444-555555555555"
TEXT = "안녕 AnyClip 👋 line1\nline2"
IMAGE_BYTES = b"\x89PNG\r\n\x1a\n" + bytes(range(64))
FILE_NAME = "réport final.txt"
FILE_BYTES = b"golden file body \x00\x01\x02"
TS = 1718000000.5
# One Korean and one accented-Latin name, binary bodies. Names NFC on wire.
FILES = [
    ("노트.txt", b"golden multi one \x00\x01"),
    ("réport (v2).bin", b"golden multi two \x02\x03"),
]
# One folder tree (Korean top folder + a nested subdir) PLUS one loose file in
# the SAME clip, so the vector pins both entry shapes: with "path" and without.
FILES_WITH_PATH = [
    ("메모.txt", b"golden tree one \x00", "보고서/메모.txt"),
    ("réport (v2).bin", b"golden tree two \x01", "보고서/sub dir/réport (v2).bin"),
    ("loose.txt", b"golden loose \x02", None),
]


def _agg(hexes: list) -> str:
    return hashlib.sha256("".join(sorted(hexes)).encode("ascii")).hexdigest()


def frame(obj: dict) -> bytes:
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    return len(data).to_bytes(4, "big") + data


def _entry(name: str, body: bytes, path) -> dict:
    """One files-clip entry in the canonical key order; "path" is appended
    last and omitted entirely for a loose file."""
    ent = {
        "name": unicodedata.normalize("NFC", name),
        "content": base64.b64encode(body).decode("ascii"),
        "hash": hashlib.sha256(body).hexdigest(),
        "bytes": len(body),
    }
    if path is not None:
        ent["path"] = unicodedata.normalize("NFC", path)
    return ent


def vectors() -> dict:
    """Every golden frame, keyed by fixture filename. Pure: main() writes."""
    return {
        # protocol_minor 0 -> 3: the spec moves the hello fixture to the
        # current feature level along with the new files-with-path vector.
        "hello.bin": {
            "type": "hello", "token": TOKEN_HASH, "node_id": NODE_ID,
            "name": "golden-mac", "version": 1, "app_version": "1.0.0",
            "protocol_major": 1, "protocol_minor": 3,
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
        "clip_files.bin": {
            "type": "clip", "kind": "files",
            "files": [_entry(n, b, None) for n, b in FILES],
            "hash": _agg([hashlib.sha256(b).hexdigest() for _n, b in FILES]),
            "ts": TS,
            "bytes": sum(len(b) for _n, b in FILES),
        },
        "clip_files_path.bin": {
            "type": "clip", "kind": "files",
            "files": [_entry(n, b, p) for n, b, p in FILES_WITH_PATH],
            "hash": _agg([hashlib.sha256(b).hexdigest()
                          for _n, b, _p in FILES_WITH_PATH]),
            "ts": TS,
            "bytes": sum(len(b) for _n, b, _p in FILES_WITH_PATH),
        },
        "ping.bin": {"type": "ping", "ts": TS},
    }


def manifest() -> dict:
    return {
        "token": TOKEN, "token_hash": TOKEN_HASH, "node_id": NODE_ID,
        "text": TEXT,
        "text_hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(),
        "image_b64": base64.b64encode(IMAGE_BYTES).decode("ascii"),
        "image_hash": hashlib.sha256(IMAGE_BYTES).hexdigest(),
        "file_name": FILE_NAME,
        "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
        "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
        "files_names": [unicodedata.normalize("NFC", n) for n, _ in FILES],
        "files_hashes": [hashlib.sha256(b).hexdigest() for _n, b in FILES],
        "files_aggregate": _agg(
            [hashlib.sha256(b).hexdigest() for _n, b in FILES]),
        "files_total_bytes": sum(len(b) for _n, b in FILES),
        "files_path_names": [unicodedata.normalize("NFC", n)
                             for n, _b, _p in FILES_WITH_PATH],
        "files_path_paths": [None if p is None
                             else unicodedata.normalize("NFC", p)
                             for _n, _b, p in FILES_WITH_PATH],
        "files_path_hashes": [hashlib.sha256(b).hexdigest()
                              for _n, b, _p in FILES_WITH_PATH],
        "files_path_aggregate": _agg([hashlib.sha256(b).hexdigest()
                                      for _n, b, _p in FILES_WITH_PATH]),
        "files_path_total_bytes": sum(len(b) for _n, b, _p in FILES_WITH_PATH),
        "ts": TS,
    }


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    frames = vectors()
    for fname, obj in frames.items():
        (OUT / fname).write_bytes(frame(obj))
    (OUT / "manifest.json").write_text(
        json.dumps(manifest(), ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {len(frames) + 1} fixtures to {OUT}")


if __name__ == "__main__":
    main()
