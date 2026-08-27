"""Wire-layer tests for the multi-file clip kind ("files", protocol 1.1):
the aggregate echo-suppression hash, strict inbound decode, and the exact
send_clip wire shape/field order. Pure logic; no sockets."""
from __future__ import annotations

import asyncio
import hashlib
import unicodedata

import pytest

import anyclip


class _OpenWriter:
    """Minimal StreamWriter stand-in: send_clip only calls is_closing()."""
    def is_closing(self) -> bool:
        return False


def _capture_link():
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = _OpenWriter()
    link._send_timeout = 1.0
    sent = []
    async def fake_send(writer, obj):
        sent.append(obj)
    link._send = fake_send
    return link, sent


def test_protocol_minor_is_three_and_cap_is_five_hundred():
    # Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts 64 MiB
    # frames (tests/test_large_frames.py), >= 3 rebuilds folder trees from the
    # per-entry "path".
    assert anyclip.PROTOCOL_MINOR == 3
    assert anyclip.MAX_FILES_PER_CLIP == 500
    assert anyclip.FILE_BUDGET == 49_466_572  # formula untouched


@pytest.mark.parametrize("path,name", [
    ("docs/a.txt", "a.txt"),
    ("docs/sub dir/a.txt", "a.txt"),
    ("보고서/1분기/요약.pdf", "요약.pdf"),
    ("a.txt", "a.txt"),                                # single segment is legal
    ("/".join(["d"] * 31 + ["a.txt"]), "a.txt"),       # exactly 32 segments
])
def test_valid_wire_paths(path, name):
    assert anyclip.is_valid_wire_path(path, name)


@pytest.mark.parametrize("path,name", [
    ("/docs/a.txt", "a.txt"),                          # absolute
    ("../a.txt", "a.txt"),                             # traversal
    ("docs/../a.txt", "a.txt"),
    ("docs/./a.txt", "a.txt"),
    ("docs//a.txt", "a.txt"),                          # empty segment
    ("docs\\a.txt", "a.txt"),                          # backslash
    ("C:/docs/a.txt", "a.txt"),                        # drive letter
    ("docs/a.txt", "b.txt"),                           # last segment != name
    ("", "a.txt"),
    (None, "a.txt"),
    (42, "a.txt"),
    ("/".join(["d"] * 32 + ["a.txt"]), "a.txt"),       # 33 segments
    ("d/" + "x" * 240 + ".txt", "x" * 240 + ".txt"),   # sanitized length > 240
])
def test_invalid_wire_paths(path, name):
    assert not anyclip.is_valid_wire_path(path, name)


def test_only_nfc_paths_are_accepted_on_the_wire():
    nfc = "보고서/요약.pdf"
    nfd = unicodedata.normalize("NFD", nfc)
    assert nfd != nfc
    assert anyclip.is_valid_wire_path(nfc, "요약.pdf")
    assert not anyclip.is_valid_wire_path(
        nfd, unicodedata.normalize("NFD", "요약.pdf"))


def test_sanitize_relpath_is_per_segment():
    assert anyclip.sanitize_relpath("docs/con/a:b.txt") == "docs/_con/a_b.txt"
    assert anyclip.sanitize_relpath(
        unicodedata.normalize("NFD", "보고서/요약.pdf")) == "보고서/요약.pdf"


def test_aggregate_is_order_independent_and_known():
    ha = hashlib.sha256(b"alpha").hexdigest()
    hb = hashlib.sha256(b"beta").hexdigest()
    expected = "0cb0309affcf4f994813ec26b8afc7e0b758605a04641de9871e04363de5e6b8"
    assert anyclip.aggregate_files_hash([ha, hb]) == expected
    assert anyclip.aggregate_files_hash([hb, ha]) == expected  # order-independent


def test_decode_files_payload_valid():
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "a", "content": "YWxwaGE=", "hash": "x", "bytes": 5},
        {"name": "b", "content": "YmV0YQ==", "hash": "y", "bytes": 4},
    ]}
    assert anyclip.decode_files_payload(msg) == [
        ("a", b"alpha", None), ("b", b"beta", None),
    ]


def test_decode_files_payload_keeps_a_valid_path():
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "a.txt", "content": "YWxwaGE=", "hash": "x", "bytes": 5,
         "path": "docs/a.txt"},
        {"name": "b.txt", "content": "YmV0YQ==", "hash": "y", "bytes": 4},
    ]}
    assert anyclip.decode_files_payload(msg) == [
        ("a.txt", b"alpha", "docs/a.txt"),
        ("b.txt", b"beta", None),
    ]


@pytest.mark.parametrize("bad", [
    "../evil.txt", "/etc/evil.txt", "C:/evil.txt", "docs\\evil.txt", 7,
])
def test_decode_files_payload_falls_back_to_flat_on_a_bad_path(bad):
    """A violating path NEVER drops the frame -- that one entry goes flat."""
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "evil.txt", "content": "YWxwaGE=", "hash": "x", "bytes": 5,
         "path": bad},
    ]}
    assert anyclip.decode_files_payload(msg) == [("evil.txt", b"alpha", None)]


def test_send_clip_files_emits_path_last_and_only_when_valid():
    async def go():
        link, sent = _capture_link()
        data = [
            ("a.txt", b"alpha", "docs/a.txt"),
            ("b.txt", b"beta", None),
            ("c.txt", b"gamma", "../c.txt"),   # invalid -> field omitted
        ]
        await link.send_clip("files", data)
        entries = sent[0]["files"]
        assert list(entries[0].keys()) == [
            "name", "content", "hash", "bytes", "path"]
        assert entries[0]["path"] == "docs/a.txt"
        assert list(entries[1].keys()) == ["name", "content", "hash", "bytes"]
        assert "path" not in entries[2]
    asyncio.run(go())


def test_two_tuple_entries_still_encode_byte_identically():
    """Loose-file clips keep the exact 1.3.0 wire shape (golden vectors)."""
    async def go():
        link, sent = _capture_link()
        await link.send_clip("files", [("a.bin", b"alpha"), ("b.bin", b"beta")])
        payload = sent[0]
        assert list(payload.keys()) == [
            "type", "kind", "files", "hash", "ts", "bytes"]
        for ent in payload["files"]:
            assert list(ent.keys()) == ["name", "content", "hash", "bytes"]
    asyncio.run(go())


def test_decode_files_payload_bad_base64_drops_whole_frame():
    msg = {"type": "clip", "kind": "files", "files": [
        {"name": "a", "content": "YWxwaGE=", "bytes": 5},
        {"name": "b", "content": "not base64!!", "bytes": 0},
    ]}
    assert anyclip.decode_files_payload(msg) is None


def test_decode_files_payload_empty_or_missing_array_ignored():
    assert anyclip.decode_files_payload({"kind": "files", "files": []}) is None
    assert anyclip.decode_files_payload({"kind": "files"}) is None


def test_send_clip_files_wire_shape_and_field_order():
    async def go():
        link, sent = _capture_link()
        data = [("alpha.bin", b"alpha"), ("beta.bin", b"beta")]
        await link.send_clip("files", data)
        assert len(sent) == 1
        payload = sent[0]
        assert list(payload.keys()) == ["type", "kind", "files", "hash", "ts", "bytes"]
        assert payload["type"] == "clip" and payload["kind"] == "files"
        assert payload["bytes"] == 9  # 5 + 4
        e0 = payload["files"][0]
        assert list(e0.keys()) == ["name", "content", "hash", "bytes"]
        assert e0["name"] == "alpha.bin"
        assert e0["content"] == "YWxwaGE="
        assert e0["hash"] == hashlib.sha256(b"alpha").hexdigest()
        assert e0["bytes"] == 5
        expected_agg = anyclip.aggregate_files_hash([
            hashlib.sha256(b"alpha").hexdigest(),
            hashlib.sha256(b"beta").hexdigest(),
        ])
        assert payload["hash"] == expected_agg
    asyncio.run(go())
