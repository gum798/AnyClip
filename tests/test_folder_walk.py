"""Sender-side folder expansion: recursive walk, junk/symlink exclusion,
deterministic byte-wise order, and the per-folder all-or-nothing admission.
Pure logic against a real tmp_path tree; no clipboard, no sockets."""
from __future__ import annotations

import logging
import os
import sys
import unicodedata

import pytest

import anyclip
from anyclip import expand_folder, folder_fits


def test_walk_is_recursive_sorted_and_relative(tmp_path):
    folder = tmp_path / "docs"
    (folder / "sub").mkdir(parents=True)
    (folder / "empty").mkdir()
    (folder / "b.txt").write_bytes(b"bb")
    (folder / "a.txt").write_bytes(b"a")
    (folder / "sub" / "c.txt").write_bytes(b"ccc")

    entries = expand_folder(str(folder))
    assert [rel for _p, _s, _m, rel in entries] == [
        "docs/a.txt", "docs/b.txt", "docs/sub/c.txt",
    ]
    assert [size for _p, size, _m, _rel in entries] == [1, 2, 3]
    assert all(os.path.isabs(p) for p, _s, _m, _rel in entries)
    assert all(anyclip.is_valid_wire_path(rel, os.path.basename(p))
               for p, _s, _m, rel in entries)


def test_junk_sidecar_files_are_excluded(tmp_path):
    folder = tmp_path / "docs"
    folder.mkdir()
    (folder / "keep.txt").write_bytes(b"k")
    for junk in (".DS_Store", "Thumbs.db", "desktop.ini"):
        (folder / junk).write_bytes(b"junk")
    assert [rel for _p, _s, _m, rel in expand_folder(str(folder))] == [
        "docs/keep.txt"]


@pytest.mark.skipif(sys.platform == "win32",
                    reason="symlink creation needs admin on Windows")
def test_symlinks_are_never_followed(tmp_path):
    outside = tmp_path / "outside.txt"
    outside.write_bytes(b"o")
    folder = tmp_path / "docs"
    folder.mkdir()
    (folder / "keep.txt").write_bytes(b"k")
    (folder / "link.txt").symlink_to(outside)
    (folder / "loop").symlink_to(tmp_path, target_is_directory=True)
    assert [rel for _p, _s, _m, rel in expand_folder(str(folder))] == [
        "docs/keep.txt"]


def test_empty_folder_expands_to_nothing(tmp_path):
    folder = tmp_path / "hollow"
    (folder / "inner").mkdir(parents=True)
    assert expand_folder(str(folder)) == []


def test_nfd_folder_and_file_names_travel_as_nfc(tmp_path):
    folder = tmp_path / unicodedata.normalize("NFD", "보고서")
    folder.mkdir()
    (folder / unicodedata.normalize("NFD", "요약.pdf")).write_bytes(b"x")
    rels = [rel for _p, _s, _m, rel in expand_folder(str(folder))]
    assert rels == ["보고서/요약.pdf"]
    assert anyclip.is_valid_wire_path(rels[0], "요약.pdf")


def test_folder_fits_is_all_or_nothing_on_budget_and_count(monkeypatch):
    entries = [("/a", 6, 0, "d/a"), ("/b", 6, 0, "d/b")]
    monkeypatch.setattr(anyclip, "FILE_BUDGET", 12)
    assert folder_fits(entries, total=0, count=0)      # exactly the budget
    assert not folder_fits(entries, total=1, count=0)  # one byte over
    monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 2)
    assert folder_fits(entries, total=0, count=0)
    assert not folder_fits(entries, total=0, count=1)  # count overflow
    assert not folder_fits([], total=0, count=0)       # empty folder


@pytest.mark.skipif(
    sys.platform == "win32" or os.geteuid() == 0,
    reason="chmod 000 is not enforceable on Windows or as root",
)
def test_unreadable_subdir_is_logged_not_silent(tmp_path, caplog):
    """os.walk swallows scandir errors by default, which would ship a PARTIAL
    tree looking complete. A partial tree is still allowed (same policy as an
    unreadable FILE) but it must never be SILENT."""
    folder = tmp_path / "docs"
    folder.mkdir()
    (folder / "keep.txt").write_bytes(b"k")
    locked = folder / "locked"
    locked.mkdir()
    (locked / "hidden.txt").write_bytes(b"h")
    locked.chmod(0o000)
    try:
        caplog.set_level(logging.WARNING, logger="anyclip")
        entries = expand_folder(str(folder))
        # The readable part still ships...
        assert [rel for _p, _s, _m, rel in entries] == ["docs/keep.txt"]
        # ...but the vanished subtree is reported.
        assert "folder walk error" in caplog.text
        assert "subtree skipped" in caplog.text
        assert "locked" in caplog.text
    finally:
        locked.chmod(0o700)


def test_walk_stops_early_once_the_absolute_file_cap_is_blown(
    tmp_path, monkeypatch,
):
    """A folder past MAX_FILES_PER_CLIP can never fit ANY remaining budget, so
    the walk bails instead of re-walking a huge tree on every poll. What it
    keeps must still be enough for folder_fits() to reject it."""
    folder = tmp_path / "big"
    folder.mkdir()
    for i in range(20):
        (folder / f"{i:02d}.txt").write_bytes(b"x")
    monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 3)

    entries = expand_folder(str(folder))
    assert len(entries) == 4  # cap + 1, then it bails
    assert not folder_fits(entries, total=0, count=0)


def test_walk_stops_early_once_the_absolute_budget_is_blown(
    tmp_path, monkeypatch,
):
    folder = tmp_path / "big"
    folder.mkdir()
    for i in range(20):
        (folder / f"{i:02d}.txt").write_bytes(b"1234")  # 4 bytes each
    monkeypatch.setattr(anyclip, "FILE_BUDGET", 10)

    entries = expand_folder(str(folder))
    assert len(entries) == 3  # 4 + 4 + 4 = 12 > 10 -> stop
    assert not folder_fits(entries, total=0, count=0)


def test_early_out_is_deterministic_across_repeated_walks(tmp_path, monkeypatch):
    """The truncated prefix must be stable, or the fingerprint would differ on
    every poll and re-toast an unsendable folder forever."""
    folder = tmp_path / "big"
    (folder / "sub").mkdir(parents=True)
    for i in range(20):
        (folder / f"{i:02d}.txt").write_bytes(b"x")
        (folder / "sub" / f"{i:02d}.txt").write_bytes(b"x")
    monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 5)
    assert expand_folder(str(folder)) == expand_folder(str(folder))


def test_a_folder_exactly_at_the_cap_is_not_truncated(tmp_path, monkeypatch):
    """The early-out is strictly ABOVE the cap: a folder of exactly
    MAX_FILES_PER_CLIP files still fits and must survive intact."""
    folder = tmp_path / "exact"
    folder.mkdir()
    for i in range(3):
        (folder / f"{i}.txt").write_bytes(b"x")
    monkeypatch.setattr(anyclip, "MAX_FILES_PER_CLIP", 3)

    entries = expand_folder(str(folder))
    assert len(entries) == 3
    assert folder_fits(entries, total=0, count=0)
