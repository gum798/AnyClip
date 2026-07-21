"""Filename sanitizer (denylist, not whitelist) + within-batch uniquify.
Keep in lockstep with Swift sanitizeFilename and C# SanitizeFilename."""
from __future__ import annotations

import unicodedata

import pytest

from anyclip import sanitize_filename, uniquify_names


def test_korean_business_name_survives_unchanged():
    name = "(E&S)_SCM 마스터플랜_20250915_공유6.pptx"
    assert sanitize_filename(name) == name


@pytest.mark.parametrize("raw,expected", [
    ("../x", "x"),
    ("..", "received.bin"),
    (".", "received.bin"),
    ("", "received.bin"),
    ("a/b\\c.txt", "c.txt"),
    ('a<b>c:"d|e?f*g.txt', "a_b_c__d_e_f_g.txt"),
    ("trail...  ", "trail"),
    ("con", "_con"),
    ("COM1.txt", "_COM1.txt"),
    ("LPT9", "_LPT9"),
    ("nul.txt", "_nul.txt"),
    ("note\x01\x7f.txt", "note__.txt"),
    ("bad:name?.txt", "bad_name_.txt"),
])
def test_sanitize_cases(raw, expected):
    assert sanitize_filename(raw) == expected


def test_nfd_korean_normalized_to_nfc():
    nfc = "받은파일.txt"
    nfd = unicodedata.normalize("NFD", nfc)
    assert nfd != nfc
    assert sanitize_filename(nfd) == nfc


def test_uniquify_with_extension():
    assert uniquify_names(["a.txt", "a.txt", "a.txt"]) == \
        ["a.txt", "a (2).txt", "a (3).txt"]


def test_uniquify_without_extension():
    assert uniquify_names(["b", "b"]) == ["b", "b (2)"]


def test_uniquify_multi_dot_before_last_extension():
    assert uniquify_names(["archive.tar.gz", "archive.tar.gz"]) == \
        ["archive.tar.gz", "archive.tar (2).gz"]


def test_uniquify_no_collision_untouched():
    assert uniquify_names(["a.txt", "b.txt"]) == ["a.txt", "b.txt"]


def test_uniquify_dotfile_treated_as_no_extension():
    assert uniquify_names([".env", ".env"]) == [".env", ".env (2)"]


def test_uniquify_guards_against_existing_names():
    assert uniquify_names(["a (2).txt", "a.txt", "a.txt"]) == \
        ["a (2).txt", "a.txt", "a (3).txt"]
