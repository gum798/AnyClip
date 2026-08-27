"""64 MiB wire frames (protocol 1.2) and the per-link legacy send gate.

The frame cap moved 16 MiB -> 64 MiB so a ~16 MB pptx syncs. Peers still on
protocol < 1.2 enforce the old 16 MiB receive cap and CLOSE the session on a
bigger frame, so the broadcast fan-out must gate per link: encode the payload
variant chosen for that link once, and skip (never drop) any link whose peer
minor is < 2 when that frame exceeds LEGACY_MAX_PAYLOAD.
"""
from __future__ import annotations

import asyncio
import logging
import types

import pytest

import anyclip
from anyclip import LinkManager


def _cfg(name="me", port=0):
    return types.SimpleNamespace(token="tok", name=name, port=port)


class FakeLink:
    """Duck-typed PeerLink for the fan-out gate: records pre-encoded frames."""

    def __init__(self, minor=2, name="peer", fail=False):
        self.peer_protocol_minor = minor
        self.peer_name = name
        self.remote_addr = None
        self.active = True
        self.closed = False
        self.frames = []
        self._fail = fail

    async def send_frame(self, data):
        if self._fail:
            raise ConnectionError("boom")
        self.frames.append(data)

    async def close(self):
        self.closed = True
        self.active = False


class CountingWriter:
    """StreamWriter stand-in that records every write and drains instantly."""

    def __init__(self):
        self.written = bytearray()
        self.closed = False

    def write(self, data):
        self.written += data

    async def drain(self):
        return

    def is_closing(self):
        return self.closed

    def close(self):
        self.closed = True


class HeaderReader:
    """Feeds a crafted 4-byte length prefix, then a tiny body for ANY length.

    Lets the length guard be exercised at 64 MiB without allocating 64 MiB.
    """

    def __init__(self, n, body=b'{"type":"ping"}'):
        self._head = n.to_bytes(4, "big")
        self._body = body
        self.head_read = False
        self.body_requested = None

    async def readexactly(self, n):
        if not self.head_read:
            self.head_read = True
            return self._head
        self.body_requested = n
        return self._body


def _bare_link(writer=None, send_timeout=1.0):
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = writer
    link._send_timeout = send_timeout
    return link


# ---- constants ---------------------------------------------------------

def test_frame_caps_and_protocol_minor():
    assert anyclip.MAX_PAYLOAD == 64 * 1024 * 1024 == 67108864
    assert anyclip.LEGACY_MAX_PAYLOAD == 16 * 1024 * 1024 == 16777216
    # Cumulative feature level: >= 1 files, >= 2 64 MiB frames (this file),
    # >= 3 rebuilds folder trees from the per-entry "path".
    assert anyclip.PROTOCOL_MINOR == 3


def test_file_budget_formula_unchanged_against_new_cap():
    assert anyclip.FILE_BUDGET == int((anyclip.MAX_PAYLOAD - 256 * 1024) * 0.74)
    assert anyclip.FILE_BUDGET == 49466572


# ---- send timeout scales with payload ----------------------------------

def test_send_timeout_scales_at_one_mib_per_second():
    assert anyclip.send_timeout_for(0) == anyclip.SEND_TIMEOUT
    assert anyclip.send_timeout_for(1024 * 1024) == anyclip.SEND_TIMEOUT + 1.0
    # Worst case must stay under the 90s per-link staleness deadline.
    assert anyclip.send_timeout_for(anyclip.MAX_PAYLOAD) == pytest.approx(74.0)
    assert anyclip.send_timeout_for(anyclip.MAX_PAYLOAD) < 30.0 * 3.0


def test_send_timeout_honours_a_custom_base():
    assert anyclip.send_timeout_for(2 * 1024 * 1024, base=0.5) == pytest.approx(2.5)


# ---- receive guard boundary --------------------------------------------

def test_recv_accepts_frame_exactly_at_max_payload():
    async def go():
        reader = HeaderReader(anyclip.MAX_PAYLOAD)
        msg = await _bare_link()._recv(reader)
        assert msg == {"type": "ping"}
        assert reader.body_requested == anyclip.MAX_PAYLOAD
    asyncio.run(go())


def test_recv_rejects_frame_one_byte_over_max_payload(caplog):
    async def go():
        caplog.set_level(logging.WARNING, logger="anyclip")
        reader = HeaderReader(anyclip.MAX_PAYLOAD + 1)
        assert await _bare_link()._recv(reader) is None
        assert reader.body_requested is None  # never tried to read the body
        assert "invalid frame length" in caplog.text
    asyncio.run(go())


def test_recv_accepts_a_frame_above_the_legacy_cap():
    async def go():
        reader = HeaderReader(anyclip.LEGACY_MAX_PAYLOAD + 1)
        assert await _bare_link()._recv(reader) == {"type": "ping"}
    asyncio.run(go())


# ---- encode guard ------------------------------------------------------

def test_send_drops_payload_over_max_payload(caplog):
    async def go():
        caplog.set_level(logging.WARNING, logger="anyclip")
        writer = CountingWriter()
        link = _bare_link(writer)
        await link.send_frame(b"x" * (anyclip.MAX_PAYLOAD + 1))
        assert bytes(writer.written) == b""  # nothing hit the socket
        assert not writer.closed             # over-cap is a drop, not a teardown
        assert "payload too large" in caplog.text
    asyncio.run(go())


def test_send_writes_a_frame_between_the_legacy_and_new_caps():
    async def go():
        writer = CountingWriter()
        body = b"y" * (anyclip.LEGACY_MAX_PAYLOAD + 5)
        await _bare_link(writer).send_frame(body)
        assert len(writer.written) == 4 + len(body)
        assert int.from_bytes(bytes(writer.written[:4]), "big") == len(body)
    asyncio.run(go())


# ---- per-link legacy gate: simple clips --------------------------------

def test_oversize_text_reaches_only_the_protocol_12_peer(caplog):
    async def go():
        caplog.set_level(logging.INFO, logger="anyclip")
        mgr = LinkManager(_cfg(), "node-self", None)
        old = FakeLink(minor=1, name="old")
        new = FakeLink(minor=2, name="new")
        mgr._links = {"o": old, "n": new}
        big = "x" * (anyclip.LEGACY_MAX_PAYLOAD + 1024)

        sent, skipped = await mgr.broadcast_clip("text", big)

        assert sent == 1
        assert skipped == ["old"]
        assert old.frames == []
        assert len(new.frames) == 1
        assert len(new.frames[0]) > anyclip.LEGACY_MAX_PAYLOAD
        # Skipped link stays up: not dropped, not closed.
        assert old.active and not old.closed
        assert ("clip too large for 'old' (peer protocol < 1.2); skipping"
                in caplog.text)
    asyncio.run(go())


def test_minor_zero_peer_is_also_gated_on_simple_clips():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        old = FakeLink(minor=0, name="ancient")
        mgr._links = {"o": old}
        sent, skipped = await mgr.broadcast_clip(
            "text", "x" * (anyclip.LEGACY_MAX_PAYLOAD + 1024))
        assert sent == 0 and skipped == ["ancient"]
        assert old.frames == [] and old.active
    asyncio.run(go())


def test_under_the_legacy_cap_everyone_gets_the_clip():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        old = FakeLink(minor=0, name="old")
        new = FakeLink(minor=2, name="new")
        mgr._links = {"o": old, "n": new}
        sent, skipped = await mgr.broadcast_clip("text", "hello")
        assert sent == 2 and skipped == []
        assert len(old.frames) == 1 and len(new.frames) == 1
    asyncio.run(go())


def test_payload_is_encoded_once_per_broadcast():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        a = FakeLink(minor=2, name="a")
        b = FakeLink(minor=2, name="b")
        mgr._links = {"a": a, "b": b}
        await mgr.broadcast_clip("text", "shared")
        # Same bytes object handed to both links -> encoded exactly once.
        assert a.frames[0] is b.frames[0]
    asyncio.run(go())


def test_oversize_image_is_gated_too():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        old = FakeLink(minor=1, name="old")
        new = FakeLink(minor=2, name="new")
        mgr._links = {"o": old, "n": new}
        png = bytes(13_000_000)  # base64 ~17.3 MB > legacy cap
        sent, skipped = await mgr.broadcast_clip("image", png)
        assert sent == 1 and skipped == ["old"]
        assert old.frames == [] and old.active
    asyncio.run(go())


# ---- per-link legacy gate: files variants ------------------------------

def test_first_file_fallback_variant_is_gated_for_a_minor_zero_peer(caplog):
    async def go():
        caplog.set_level(logging.INFO, logger="anyclip")
        mgr = LinkManager(_cfg(), "node-self", None)
        old = FakeLink(minor=0, name="old")   # gets the first-file variant
        new = FakeLink(minor=2, name="new")   # gets the full files variant
        mgr._links = {"o": old, "n": new}
        # The FIRST file alone exceeds the legacy cap once base64'd, so the
        # fallback variant chosen for the minor-0 link is what gets gated.
        data = [("big.bin", bytes(13_000_000)), ("small.txt", b"hi")]

        full, fallback, dropped, skipped = await mgr.broadcast_files(data)

        assert full == 1 and fallback == 0 and dropped == 0
        assert skipped == ["old"]
        assert old.frames == [] and old.active and not old.closed
        assert len(new.frames) == 1
        assert ("clip too large for 'old' (peer protocol < 1.2); skipping"
                in caplog.text)
    asyncio.run(go())


def test_minor_one_peer_is_gated_on_an_oversize_files_clip():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        mid = FakeLink(minor=1, name="mid")   # takes kind:"files" but not >16MiB
        new = FakeLink(minor=2, name="new")
        mgr._links = {"m": mid, "n": new}
        data = [("big.bin", bytes(13_000_000)), ("small.txt", b"hi")]
        full, fallback, dropped, skipped = await mgr.broadcast_files(data)
        assert full == 1 and fallback == 0
        assert skipped == ["mid"]
        assert mid.frames == [] and mid.active
    asyncio.run(go())


def test_small_files_clip_still_fans_out_with_minor_gating():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        new = FakeLink(minor=2, name="new")
        old = FakeLink(minor=0, name="old")
        mgr._links = {"n": new, "o": old}
        data = [("a.txt", b"one"), ("b.txt", b"two"), ("c.txt", b"three")]
        full, fallback, dropped, skipped = await mgr.broadcast_files(data)
        assert full == 1 and fallback == 1 and dropped == 2 and skipped == []
        assert len(new.frames) == 1 and len(old.frames) == 1
    asyncio.run(go())


def test_files_variants_are_each_encoded_once():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        n1, n2 = FakeLink(minor=2, name="n1"), FakeLink(minor=1, name="n2")
        o1, o2 = FakeLink(minor=0, name="o1"), FakeLink(minor=0, name="o2")
        mgr._links = {"a": n1, "b": n2, "c": o1, "d": o2}
        data = [("a.txt", b"one"), ("b.txt", b"two")]
        await mgr.broadcast_files(data)
        assert n1.frames[0] is n2.frames[0]  # one "files" variant encode
        assert o1.frames[0] is o2.frames[0]  # one "file" fallback encode
        assert n1.frames[0] is not o1.frames[0]
    asyncio.run(go())


# ---- aggregated skip toast ---------------------------------------------

def test_size_skip_message_is_at_most_one_per_clip():
    assert anyclip.size_skip_message([]) is None
    assert anyclip.size_skip_message(["MacBook"]) == (
        "clip not sent to MacBook (too large for its AnyClip version)"
    )
    assert anyclip.size_skip_message(["MacBook", "PC", "NUC"]) == (
        "clip not sent to 3 peer(s) (too large for their AnyClip version)"
    )
