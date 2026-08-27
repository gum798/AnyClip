"""Sender-side folder expansion: recursive walk, junk/symlink exclusion,
deterministic byte-wise order, and the per-folder all-or-nothing admission.
Pure logic against a real tmp_path tree; no clipboard, no sockets."""
from __future__ import annotations

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
