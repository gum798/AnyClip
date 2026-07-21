"""The clip_files.bin golden vector decodes to the manifest's recorded names,
per-file hashes, aggregate, and total bytes -- proving Python's encoding is
the canonical source the Swift/C# suites assert against."""
from __future__ import annotations

import base64
import hashlib
import json
import pathlib

import anyclip

FIX = (pathlib.Path(__file__).resolve().parent.parent
       / "formacOS" / "Tests" / "AnyClipCoreTests" / "Fixtures")


def _decode_frame(path: pathlib.Path) -> dict:
    raw = path.read_bytes()
    n = int.from_bytes(raw[:4], "big")
    assert len(raw) == 4 + n, "length prefix must match body length"
    return json.loads(raw[4:].decode("utf-8"))


def test_clip_files_golden_matches_manifest():
    manifest = json.loads((FIX / "manifest.json").read_text(encoding="utf-8"))
    obj = _decode_frame(FIX / "clip_files.bin")
    assert list(obj.keys()) == ["type", "kind", "files", "hash", "ts", "bytes"]
    assert obj["type"] == "clip" and obj["kind"] == "files"

    names, hashes, total = [], [], 0
    for ent in obj["files"]:
        assert list(ent.keys()) == ["name", "content", "hash", "bytes"]
        data = base64.b64decode(ent["content"], validate=True)
        recomputed = hashlib.sha256(data).hexdigest()
        assert recomputed == ent["hash"]  # wire hash matches recomputed
        assert ent["bytes"] == len(data)
        names.append(ent["name"])
        hashes.append(recomputed)
        total += len(data)

    assert names == manifest["files_names"]
    assert hashes == manifest["files_hashes"]
    assert obj["bytes"] == total == manifest["files_total_bytes"]
    agg = anyclip.aggregate_files_hash(hashes)
    assert agg == obj["hash"] == manifest["files_aggregate"]
