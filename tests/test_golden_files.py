"""The clip_files.bin golden vector decodes to the manifest's recorded names,
per-file hashes, aggregate, and total bytes -- proving Python's encoding is
the canonical source the Swift/C# suites assert against."""
from __future__ import annotations

import base64
import hashlib
import importlib.util
import json
import pathlib
import unicodedata

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


def _generator():
    """Import formacOS/Scripts/gen-golden-vectors.py as a module. Module-level
    code only computes constants (main() is behind __main__), so importing it
    never writes to the committed Fixtures directory."""
    path = (pathlib.Path(__file__).resolve().parent.parent
            / "formacOS" / "Scripts" / "gen-golden-vectors.py")
    spec = importlib.util.spec_from_file_location("gen_golden_vectors", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


def test_files_with_path_vector_matches_the_canonical_encoder():
    """The new golden frame must be exactly what anyclip's encoder produces --
    Python is the canonical source both native suites assert against."""
    gen = _generator()
    obj = gen.vectors()["clip_files_path.bin"]
    canonical = anyclip.build_clip_payload("files", [
        (unicodedata.normalize("NFC", n), b,
         None if p is None else unicodedata.normalize("NFC", p))
        for n, b, p in gen.FILES_WITH_PATH
    ])
    assert list(obj.keys()) == list(canonical.keys())
    assert obj["files"] == canonical["files"]
    assert obj["hash"] == canonical["hash"]
    assert obj["bytes"] == canonical["bytes"]
    # Folder entries carry "path" LAST; the loose entry carries none at all.
    assert list(obj["files"][0].keys()) == [
        "name", "content", "hash", "bytes", "path"]
    assert anyclip.is_valid_wire_path(
        obj["files"][0]["path"], obj["files"][0]["name"])
    assert obj["files"][1]["path"].count("/") == 2   # nested subdirectory
    assert "path" not in obj["files"][2]             # loose file in the clip


def test_hello_vector_advertises_the_current_protocol_minor():
    """The spec calls for the hello fixture to move to minor 3 alongside the
    new files-with-path vector; pin it here so the regeneration in the Swift
    task cannot silently ship the stale 0."""
    assert _generator().vectors()["hello.bin"]["protocol_minor"] == 3
    assert anyclip.PROTOCOL_MINOR == 3


def test_files_with_path_manifest_records_the_same_paths():
    gen = _generator()
    man = gen.manifest()
    obj = gen.vectors()["clip_files_path.bin"]
    assert man["files_path_paths"] == [
        ent.get("path") for ent in obj["files"]]
    assert man["files_path_names"] == [ent["name"] for ent in obj["files"]]
    assert man["files_path_aggregate"] == obj["hash"]
    assert man["files_path_total_bytes"] == obj["bytes"]
