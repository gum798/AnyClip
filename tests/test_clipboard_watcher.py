"""Tests for ClipboardWatcher's file-clipboard path.

All clipboard helpers are monkeypatched so no real clipboard access
happens (the constructor seeds baselines from them); tmp_path supplies
real files/directories so the stat/read code paths run for real.
"""

from __future__ import annotations

import os
import sys

import pytest

import anyclip
from anyclip import ClipboardWatcher


@pytest.fixture(autouse=True)
def quiet_clipboard(monkeypatch):
    """Neutralise every clipboard read the watcher performs."""
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [])
    monkeypatch.setattr(anyclip, "grab_clipboard_image", lambda: None)
    monkeypatch.setattr(anyclip.pyperclip, "paste", lambda: "")


def _make_watcher(on_change=None, on_file_skipped=None) -> ClipboardWatcher:
    async def _noop(kind, data) -> None:
        return None

    return ClipboardWatcher(
        0.01, on_change or _noop, on_file_skipped=on_file_skipped,
    )


@pytest.mark.asyncio
async def test_directory_skipped_with_single_notice(monkeypatch, tmp_path):
    """A directory on the clipboard is skipped once, with feedback,
    and is NOT retried on subsequent polls (regression: infinite
    EISDIR retry loop)."""
    changes = []
    skipped = []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "TODO"
    folder.mkdir()
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert changes == []
    assert len(skipped) == 1
    assert "TODO" in skipped[0]

    # Second poll with the same directory: fingerprint must have been
    # updated, so no re-detection, no second notice.
    await watcher._check_file_clipboard()
    assert changes == []
    assert len(skipped) == 1


@pytest.mark.asyncio
async def test_directory_skipped_without_callback(monkeypatch, tmp_path):
    """No on_file_skipped wired (headless/--no-notify): still no crash,
    still no retry loop."""
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    folder = tmp_path / "stuff"
    folder.mkdir()
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    await watcher._check_file_clipboard()
    assert changes == []
    assert watcher._last_file_fp is not None


@pytest.mark.asyncio
async def test_regular_file_still_synced(monkeypatch, tmp_path):
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    target = tmp_path / "note.txt"
    target.write_bytes(b"hello")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(target)])

    await watcher._check_file_clipboard()
    assert changes == [("file", ("note.txt", b"hello"))]


@pytest.mark.skipif(
    sys.platform == "win32" or os.geteuid() == 0,
    reason="chmod 000 is not enforceable on Windows or as root",
)
@pytest.mark.asyncio
async def test_unreadable_file_does_not_loop(monkeypatch, tmp_path):
    """Any read failure must update the fingerprint so the watcher
    does not retry the same path every poll."""
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    target = tmp_path / "secret.bin"
    target.write_bytes(b"x")
    target.chmod(0o000)
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(target)])
    try:
        await watcher._check_file_clipboard()
        first_fp = watcher._last_file_fp
        assert changes == []
        assert first_fp is not None  # fingerprint recorded despite failure

        await watcher._check_file_clipboard()
        assert changes == []
        assert watcher._last_file_fp == first_fp
    finally:
        target.chmod(0o600)
