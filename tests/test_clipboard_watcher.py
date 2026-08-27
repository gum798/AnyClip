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


def _sparse_file(path, size: int):
    """A file of exactly `size` bytes without writing `size` bytes, so the
    real ~49 MB FILE_BUDGET boundary can be exercised cheaply."""
    with open(path, "wb") as fh:
        fh.truncate(size)
    return path


def _make_watcher(on_change=None, on_file_skipped=None) -> ClipboardWatcher:
    async def _noop(kind, data) -> None:
        return None

    return ClipboardWatcher(
        0.01, on_change or _noop, on_file_skipped=on_file_skipped,
    )


@pytest.mark.asyncio
async def test_empty_folder_toasts_and_sends_nothing(monkeypatch, tmp_path):
    """A folder with nothing syncable in it toasts once and is NOT retried
    on subsequent polls (regression: infinite EISDIR retry loop)."""
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "Inbox"
    folder.mkdir()
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert changes == []
    assert skipped == ["folder is empty; nothing to sync"]

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


@pytest.mark.asyncio
async def test_decomposed_filename_sent_as_nfc(monkeypatch, tmp_path):
    """macOS hands filenames to us in NFD (decomposed Hangul = conjoining
    jamo U+11xx). We must send them NFC or a Windows peer renders broken
    glyphs instead of the name. (Mac → Windows filename corruption.)"""
    import unicodedata

    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    nfc_name = "결과보고서.pdf"
    nfd_name = unicodedata.normalize("NFD", nfc_name)
    assert nfd_name != nfc_name  # the two forms genuinely differ
    target = tmp_path / nfd_name
    target.write_bytes(b"data")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(target)])

    await watcher._check_file_clipboard()
    assert len(changes) == 1
    sent_name, sent_data = changes[0][1]
    assert sent_name == nfc_name  # sent composed, not decomposed
    assert sent_data == b"data"


def test_received_filename_normalized_to_nfc(monkeypatch, tmp_path):
    """A file received from a macOS peer (NFD name) is written and placed on
    the clipboard with an NFC name, so it is not corrupted on Windows."""
    import unicodedata

    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    captured = {}

    def fake_set(path):
        captured["path"] = path
        return True

    monkeypatch.setattr(anyclip, "set_clipboard_file", fake_set)
    watcher = _make_watcher()
    nfc = "받은파일.txt"
    nfd = unicodedata.normalize("NFD", nfc)
    assert nfd != nfc
    ok = watcher.update_local_file(nfd, b"data")
    assert ok
    assert os.path.basename(captured["path"]) == nfc  # composed on disk + clipboard


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


@pytest.mark.asyncio
async def test_multiple_files_emitted_as_files_kind(monkeypatch, tmp_path):
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"one")
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"two")
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.txt", b"one", None), ("b.txt", b"two", None)])]

    # Same selection again -> fingerprint list matches -> no second emission.
    await watcher._check_file_clipboard()
    assert len(changes) == 1


@pytest.mark.asyncio
async def test_folder_mixed_with_files(monkeypatch, tmp_path):
    """A folder now EXPANDS instead of being skipped; loose files in the same
    selection keep their flat (pathless) entries, in selection order."""
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "docs"
    (folder / "sub").mkdir(parents=True)
    (folder / "one.txt").write_bytes(b"1")
    (folder / "sub" / "two.txt").write_bytes(b"2")
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"one")
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(folder), str(f1)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [
        ("one.txt", b"1", "docs/one.txt"),
        ("two.txt", b"2", "docs/sub/two.txt"),
        ("a.txt", b"one", None),
    ])]
    assert skipped == []


@pytest.mark.asyncio
async def test_budget_greedy_partial_send(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"123456")   # 6, accepted (total 6)
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"789012")   # 6, 6+6=12>10 skip
    f3 = tmp_path / "c.txt"; f3.write_bytes(b"XY")       # 2, 6+2=8<=10 accepted
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2), str(f3)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.txt", b"123456", None), ("c.txt", b"XY", None)])]
    assert any("skipped" in m for m in skipped)


@pytest.mark.asyncio
async def test_sum_exactly_at_budget_is_accepted(monkeypatch, tmp_path):
    """Greedy accept is `<= FILE_BUDGET`: a selection summing to exactly the
    budget goes out whole, one more byte drops the last file."""
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    budget = 12
    monkeypatch.setattr(anyclip, "FILE_BUDGET", budget)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f1 = tmp_path / "a.bin"; f1.write_bytes(b"a" * (budget // 2))
    f2 = tmp_path / "b.bin"; f2.write_bytes(b"b" * (budget - budget // 2))
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("a.bin", b"a" * 6, None), ("b.bin", b"b" * 6, None)])]
    assert skipped == []


@pytest.mark.asyncio
async def test_single_file_at_the_real_budget_is_accepted(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f = _sparse_file(tmp_path / "at-budget.bin", anyclip.FILE_BUDGET)
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(f)])

    await watcher._check_file_clipboard()
    assert len(changes) == 1
    kind, (name, data) = changes[0]
    assert kind == "file" and name == "at-budget.bin"
    assert len(data) == anyclip.FILE_BUDGET
    assert skipped == []


@pytest.mark.asyncio
async def test_single_file_one_byte_over_the_real_budget_is_skipped(
    monkeypatch, tmp_path,
):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f = _sparse_file(tmp_path / "over-budget.bin", anyclip.FILE_BUDGET + 1)
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(f)])

    await watcher._check_file_clipboard()
    assert changes == []
    assert skipped == ["1 file(s) skipped (too large to sync)"]


@pytest.mark.asyncio
async def test_single_survivor_falls_back_to_file_kind(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    f1 = tmp_path / "a.txt"; f1.write_bytes(b"12345678")  # 8, accepted
    f2 = tmp_path / "b.txt"; f2.write_bytes(b"ABCDEFGH")  # 8, 8+8=16>10 skip
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(f1), str(f2)])

    await watcher._check_file_clipboard()
    assert changes == [("file", ("a.txt", b"12345678"))]
    assert any("skipped" in m for m in skipped)


@pytest.mark.asyncio
async def test_folder_expansion_is_not_re_detected_but_edits_are(
    monkeypatch, tmp_path,
):
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    folder = tmp_path / "docs"
    (folder / "sub").mkdir(parents=True)
    (folder / "sub" / "b.txt").write_bytes(b"two")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert len(changes) == 1
    # Unchanged selection -> the expanded fingerprint matches -> no re-send.
    await watcher._check_file_clipboard()
    assert len(changes) == 1
    # A change DEEP inside the tree does not move the top dir's mtime, but the
    # expanded fingerprint covers it, so it is picked up.
    (folder / "sub" / "b.txt").write_bytes(b"two-changed")
    await watcher._check_file_clipboard()
    assert len(changes) == 2
    assert changes[1] == ("files", [("b.txt", b"two-changed", "docs/sub/b.txt")])


@pytest.mark.asyncio
async def test_single_file_folder_stays_a_files_clip(monkeypatch, tmp_path):
    """A one-file folder must NOT collapse to the legacy kind:"file" frame --
    that frame has nowhere to carry the path."""
    changes = []

    async def on_change(kind, data):
        changes.append((kind, data))

    watcher = _make_watcher(on_change)
    folder = tmp_path / "one"
    folder.mkdir()
    (folder / "only.txt").write_bytes(b"x")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert changes == [("files", [("only.txt", b"x", "one/only.txt")])]


@pytest.mark.asyncio
async def test_folder_over_budget_is_skipped_whole(monkeypatch, tmp_path):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "docs"
    folder.mkdir()
    (folder / "a.txt").write_bytes(b"123456")
    (folder / "b.txt").write_bytes(b"789012")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert changes == []                       # no partial tree
    assert skipped == ["folder too large to sync: docs"]


@pytest.mark.asyncio
async def test_loose_files_stay_greedy_while_a_folder_is_all_or_nothing(
    monkeypatch, tmp_path,
):
    """Selection order decides: the loose file eats the budget first, then the
    folder no longer fits ENTIRELY and is dropped whole."""
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    loose = tmp_path / "a.txt"; loose.write_bytes(b"123456")
    folder = tmp_path / "docs"
    folder.mkdir()
    (folder / "x.txt").write_bytes(b"111")
    (folder / "y.txt").write_bytes(b"222")
    monkeypatch.setattr(anyclip, "grab_clipboard_files",
                        lambda: [str(loose), str(folder)])

    await watcher._check_file_clipboard()
    assert changes == [("file", ("a.txt", b"123456"))]
    assert skipped == ["folder too large to sync: docs"]


@pytest.mark.asyncio
async def test_folder_over_the_file_count_cap_is_skipped_whole(
    monkeypatch, tmp_path,
):
    changes, skipped = [], []

    async def on_change(kind, data):
        changes.append((kind, data))

    async def on_skip(message):
        skipped.append(message)

    monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 2)
    watcher = _make_watcher(on_change, on_file_skipped=on_skip)
    folder = tmp_path / "docs"
    folder.mkdir()
    for i in range(3):
        (folder / f"{i}.txt").write_bytes(b"x")
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [str(folder)])

    await watcher._check_file_clipboard()
    assert changes == []
    assert skipped == ["folder too large to sync: docs"]
