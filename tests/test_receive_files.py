"""Receive/apply + peer-minor send gating for the "files" kind."""
from __future__ import annotations

import asyncio
import os
import types

import anyclip
from anyclip import (
    ClipboardWatcher,
    EchoSuppressor,
    aggregate_files_hash,
    sha256_bytes,
)


class _FakeLink:
    def __init__(self, minor):
        self.peer_protocol_minor = minor
        self.peer_name = "peer"
        self.sent = []

    async def send_clip(self, kind, content):
        self.sent.append((kind, content))


def test_peer_protocol_minor_initialized_by_init():
    # Narrowed PeerLink now receives the parsed hello identity directly.
    link = anyclip.PeerLink(
        "node-self", "peer-node", "peer", 1,
        reader=None, writer=None, on_clip=None,
    )
    assert link.peer_protocol_minor == 1
    assert link.peer_name == "peer"
    assert link.remote_addr is None


def test_emit_files_new_peer_sends_files():
    async def go():
        link = _FakeLink(minor=1)
        data = [("a.txt", b"one"), ("b.txt", b"two")]
        assert await anyclip.emit_files_clip(link, EchoSuppressor(), data) == ("files", 2)
        assert link.sent == [("files", data)]
    asyncio.run(go())


def test_emit_files_old_peer_falls_back_to_first_file():
    async def go():
        link = _FakeLink(minor=0)
        data = [("a.txt", b"one"), ("b.txt", b"two"), ("c.txt", b"three")]
        assert await anyclip.emit_files_clip(link, EchoSuppressor(), data) == ("file", 2)
        assert link.sent == [("file", ("a.txt", b"one"))]
    asyncio.run(go())


def test_emit_files_suppresses_echo_but_not_a_different_set():
    async def go():
        link = _FakeLink(minor=1)
        sup = EchoSuppressor()
        data = [("a.txt", b"one"), ("b.txt", b"two")]
        agg = aggregate_files_hash([sha256_bytes(b"one"), sha256_bytes(b"two")])
        sup.mark_received("files", agg)
        assert await anyclip.emit_files_clip(link, sup, data) == ("suppressed", 0)
        assert link.sent == []
        other = [("a.txt", b"one"), ("b.txt", b"CHANGED")]
        decision, _ = await anyclip.emit_files_clip(link, sup, other)
        assert decision == "files"
    asyncio.run(go())


def test_update_local_files_writes_uniquifies_and_baselines(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    monkeypatch.setattr(anyclip.sys, "platform", "darwin")  # places FIRST only
    placed = {}

    def fake_set(path):
        placed["path"] = path
        return True

    monkeypatch.setattr(anyclip, "set_clipboard_file", fake_set)
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [])
    monkeypatch.setattr(anyclip, "grab_clipboard_image", lambda: None)
    monkeypatch.setattr(anyclip.pyperclip, "paste", lambda: "")

    async def _noop(kind, data):
        return None

    watcher = ClipboardWatcher(0.01, _noop)
    n = watcher.update_local_files([
        ("dup.txt", b"one"), ("dup.txt", b"two"), ("a<b.txt", b"three"),
    ])
    received = tmp_path / "received"
    assert (received / "dup.txt").read_bytes() == b"one"
    assert (received / "dup (2).txt").read_bytes() == b"two"
    assert (received / "a_b.txt").read_bytes() == b"three"  # denylist sanitized
    assert n == 1  # macOS places the first file only
    assert os.path.basename(placed["path"]) == "dup.txt"
    assert isinstance(watcher._last_file_fp, list) and len(watcher._last_file_fp) == 1
    assert watcher._last_file_fp[0][0].endswith("dup.txt")


def test_emit_files_old_peer_skips_a_folder_only_clip():
    async def go():
        link = _FakeLink(minor=0)
        data = [("a.txt", b"one", "docs/a.txt")]
        assert await anyclip.emit_files_clip(
            link, EchoSuppressor(), data) == ("skipped", 0)
        assert link.sent == []
    asyncio.run(go())
