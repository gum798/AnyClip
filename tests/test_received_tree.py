"""Receiver-side folder rebuild: destination planning (sanitize, top-segment
uniquify, flat fallback), the actual writes under received/, clipboard
placement, the received-clip toast wording, and the echo-suppressor guard
lifted out of on_remote_clip."""
from __future__ import annotations

import unicodedata

import pytest

import anyclip
from anyclip import ClipboardWatcher, plan_received_layout


@pytest.fixture(autouse=True)
def quiet_clipboard(monkeypatch):
    monkeypatch.setattr(anyclip, "grab_clipboard_files", lambda: [])
    monkeypatch.setattr(anyclip, "grab_clipboard_image", lambda: None)
    monkeypatch.setattr(anyclip.pyperclip, "paste", lambda: "")


def _make_watcher() -> ClipboardWatcher:
    async def _noop(kind, data) -> None:
        return None

    return ClipboardWatcher(0.01, _noop)


# ---- destination planning ---------------------------------------------

def test_plan_rebuilds_a_tree_and_keeps_loose_files_flat():
    files = [
        ("a.txt", b"", "docs/a.txt"),
        ("b.txt", b"", "docs/sub/b.txt"),
        ("loose.txt", b"", None),
    ]
    assert plan_received_layout(files, set()) == [
        ("docs/a.txt", "docs"),
        ("docs/sub/b.txt", "docs"),
        ("loose.txt", "loose.txt"),
    ]


def test_top_segment_uniquify_is_shared_by_every_entry_of_the_clip():
    files = [("a.txt", b"", "docs/a.txt"), ("b.txt", b"", "docs/sub/b.txt")]
    assert plan_received_layout(files, {"docs"}) == [
        ("docs-2/a.txt", "docs-2"), ("docs-2/sub/b.txt", "docs-2"),
    ]
    assert plan_received_layout(files, {"docs", "docs-2"}) == [
        ("docs-3/a.txt", "docs-3"), ("docs-3/sub/b.txt", "docs-3"),
    ]


def test_two_folders_in_one_clip_get_independent_tops():
    files = [("a", b"", "x/a"), ("b", b"", "y/b")]
    assert plan_received_layout(files, {"x"}) == [("x-2/a", "x-2"), ("y/b", "y")]


def test_every_segment_goes_through_the_per_name_sanitizer():
    files = [("a:b.txt", b"", "보고서/con/a:b.txt")]
    assert plan_received_layout(files, set()) == [
        ("보고서/_con/a_b.txt", "보고서"),
    ]


def test_loose_duplicates_keep_the_existing_uniquify():
    files = [("dup.txt", b"", None), ("dup.txt", b"", None)]
    assert plan_received_layout(files, set()) == [
        ("dup.txt", "dup.txt"), ("dup (2).txt", "dup (2).txt"),
    ]


def test_a_folder_top_never_collides_with_a_loose_file_of_the_same_clip():
    files = [("docs", b"", None), ("a.txt", b"", "docs/a.txt")]
    assert plan_received_layout(files, set()) == [
        ("docs", "docs"), ("docs-2/a.txt", "docs-2"),
    ]


@pytest.mark.parametrize("bad", [
    "../../evil.txt", "/etc/evil.txt", "C:/evil.txt", "docs\\evil.txt",
    "docs/other.txt",                                  # last segment != name
    unicodedata.normalize("NFD", "보고서") + "/evil.txt",  # not NFC
])
def test_a_violating_path_falls_back_to_flat_placement(bad):
    """Defense in depth: decode already rejects these, but the writer never
    trusts its caller -- the entry goes flat, it is never dropped."""
    assert plan_received_layout([("evil.txt", b"", bad)], set()) == [
        ("evil.txt", "evil.txt"),
    ]


# ---- writes + clipboard placement --------------------------------------

def test_tree_is_written_and_the_top_folder_is_placed(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    monkeypatch.setattr(anyclip.sys, "platform", "darwin")  # places FIRST only
    placed = {}

    def fake_set(path):
        placed["path"] = path
        return True

    monkeypatch.setattr(anyclip, "set_clipboard_file", fake_set)
    watcher = _make_watcher()

    n = watcher.update_local_files([
        ("a.txt", b"one", "docs/a.txt"),
        ("b.txt", b"two", "docs/sub/b.txt"),
    ])

    received = tmp_path / "received"
    assert (received / "docs" / "a.txt").read_bytes() == b"one"
    assert (received / "docs" / "sub" / "b.txt").read_bytes() == b"two"
    assert n == 1
    assert placed["path"] == str(received / "docs")
    # Baseline covers the placed folder AND its tree, so the next poll does
    # not echo what we just wrote.
    assert watcher._last_file_fp is not None
    assert len(watcher._last_file_fp) == 3


def test_windows_places_every_top_level_item_in_batch_order(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    monkeypatch.setattr(anyclip.sys, "platform", "win32")
    placed = {}

    def fake_set_many(paths):
        placed["paths"] = list(paths)
        return True

    monkeypatch.setattr(anyclip, "set_clipboard_files", fake_set_many)
    watcher = _make_watcher()

    n = watcher.update_local_files([
        ("a.txt", b"1", "docs/a.txt"),
        ("b.txt", b"2", "docs/sub/b.txt"),
        ("loose.txt", b"3", None),
        ("c.txt", b"4", "other/c.txt"),
    ])

    received = tmp_path / "received"
    assert n == 3
    assert placed["paths"] == [
        str(received / "docs"),
        str(received / "loose.txt"),
        str(received / "other"),
    ]


def test_a_second_clip_of_the_same_folder_lands_beside_the_first(
    monkeypatch, tmp_path,
):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    monkeypatch.setattr(anyclip.sys, "platform", "darwin")
    monkeypatch.setattr(anyclip, "set_clipboard_file", lambda p: True)
    watcher = _make_watcher()
    clip = [("a.txt", b"one", "docs/a.txt"), ("b.txt", b"two", "docs/sub/b.txt")]

    watcher.update_local_files(clip)
    watcher.update_local_files(clip)

    received = tmp_path / "received"
    assert (received / "docs" / "a.txt").exists()
    assert (received / "docs-2" / "a.txt").read_bytes() == b"one"
    assert (received / "docs-2" / "sub" / "b.txt").read_bytes() == b"two"


def test_writer_never_escapes_received(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path / "home")
    monkeypatch.setattr(anyclip.sys, "platform", "darwin")
    monkeypatch.setattr(anyclip, "set_clipboard_file", lambda p: True)
    watcher = _make_watcher()

    watcher.update_local_files([("evil.txt", b"x", "../../evil.txt")])

    assert not (tmp_path / "evil.txt").exists()
    assert (tmp_path / "home" / "received" / "evil.txt").read_bytes() == b"x"


def test_received_clip_message_names_a_folder_only_clip():
    folder = [("a", b"", "docs/a"), ("b", b"", "docs/sub/b")]
    assert anyclip.received_clip_message(folder) == "docs (2 files)"
    assert anyclip.received_clip_message(
        folder + [("c", b"", None)]) == "3 files"
    assert anyclip.received_clip_message(
        [("a", b"", "x/a"), ("b", b"", "y/b")]) == "2 files"


def test_clear_received_dir_removes_trees(monkeypatch, tmp_path):
    monkeypatch.setattr(anyclip, "LOG_DIR", tmp_path)
    received = tmp_path / "received"
    (received / "docs" / "sub").mkdir(parents=True)
    (received / "docs" / "sub" / "a.txt").write_bytes(b"x")
    (received / "loose.txt").write_bytes(b"y")

    anyclip.clear_received_dir()

    assert received.exists()
    assert list(received.iterdir()) == []


# ---- on_remote_clip's suppressor guard ---------------------------------

def test_only_a_lone_placed_loose_file_seeds_the_single_file_slot():
    """Python-macOS places only the FIRST top-level item. A lone placed LOOSE
    file re-surfaces on the next poll as kind:"file", so its slot must be
    seeded; a placed FOLDER re-surfaces as kind:"files" and must NOT seed it
    (seeding would suppress a genuine later single-file copy)."""
    loose = [("a.txt", b"one", None)]
    folder = [("a.txt", b"one", "docs/a.txt"), ("b.txt", b"two", "docs/sub/b.txt")]
    assert anyclip.placed_single_loose_file(loose, 1)
    assert not anyclip.placed_single_loose_file(loose, 0)   # nothing placed
    assert not anyclip.placed_single_loose_file(folder, 1)  # folder placed
    assert not anyclip.placed_single_loose_file([], 1)      # no entries
    # Legacy 2-tuples read as loose, exactly like entry_relpath().
    assert anyclip.placed_single_loose_file([("a.txt", b"one")], 1)
