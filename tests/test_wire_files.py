"""Wire-layer tests for the multi-file clip kind ("files", protocol 1.1):
the aggregate echo-suppression hash, strict inbound decode, and the exact
send_clip wire shape/field order. Pure logic; no sockets."""
from __future__ import annotations

import asyncio
import hashlib

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


def test_protocol_minor_bumped_to_one():
    assert anyclip.PROTOCOL_MINOR == 1


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
    assert anyclip.decode_files_payload(msg) == [("a", b"alpha"), ("b", b"beta")]


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
