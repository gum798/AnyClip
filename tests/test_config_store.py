"""Tests for the config_store deep module.

Covers the four AC properties: save->load roundtrip, 0600 permissions
(POSIX only), graceful handling of corrupt/incomplete JSON, and
generate_token() entropy.
"""

from __future__ import annotations

import json
import os
import stat
import sys
from pathlib import Path

import pytest

import config_store
from config_store import Config, generate_token, load, save


def test_roundtrip(tmp_path: Path) -> None:
    cfg = Config(token="abc-123")
    save(cfg, config_dir=tmp_path)
    loaded = load(config_dir=tmp_path)
    assert loaded == cfg


def test_load_missing_returns_none(tmp_path: Path) -> None:
    assert load(config_dir=tmp_path) is None


@pytest.mark.skipif(sys.platform == "win32", reason="POSIX permissions only")
def test_save_sets_0600(tmp_path: Path) -> None:
    save(Config(token="x"), config_dir=tmp_path)
    mode = stat.S_IMODE(os.stat(tmp_path / "config.json").st_mode)
    assert mode == 0o600


def test_corrupt_json_graceful(tmp_path: Path) -> None:
    (tmp_path / "config.json").write_text("{not valid json", encoding="utf-8")
    assert load(config_dir=tmp_path) is None


def test_non_object_json_graceful(tmp_path: Path) -> None:
    (tmp_path / "config.json").write_text("[1, 2, 3]", encoding="utf-8")
    assert load(config_dir=tmp_path) is None


def test_missing_token_field_graceful(tmp_path: Path) -> None:
    (tmp_path / "config.json").write_text('{"other": "field"}', encoding="utf-8")
    assert load(config_dir=tmp_path) is None


def test_empty_token_graceful(tmp_path: Path) -> None:
    (tmp_path / "config.json").write_text('{"token": ""}', encoding="utf-8")
    assert load(config_dir=tmp_path) is None


def test_generate_token_entropy() -> None:
    a = generate_token()
    b = generate_token()
    assert a != b
    # URL-safe base64 of 32 bytes -> >= 40 chars
    assert len(a) >= 40
    # url-safe alphabet only
    allowed = set(
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_"
    )
    assert set(a).issubset(allowed)


def test_generate_token_uniqueness_bulk() -> None:
    tokens = {generate_token() for _ in range(200)}
    assert len(tokens) == 200


def test_save_overwrites(tmp_path: Path) -> None:
    save(Config(token="first"), config_dir=tmp_path)
    save(Config(token="second"), config_dir=tmp_path)
    assert load(config_dir=tmp_path) == Config(token="second")


def test_save_creates_missing_dir(tmp_path: Path) -> None:
    nested = tmp_path / "deeper" / "nest"
    save(Config(token="x"), config_dir=nested)
    assert (nested / "config.json").exists()


def test_save_does_not_leave_tempfile(tmp_path: Path) -> None:
    save(Config(token="x"), config_dir=tmp_path)
    leftovers = [p for p in tmp_path.iterdir() if p.name != "config.json"]
    assert leftovers == []


def test_load_json_with_extra_fields_ok(tmp_path: Path) -> None:
    """Extra unknown keys are ignored (forward-compat)."""
    (tmp_path / "config.json").write_text(
        json.dumps({"token": "tok", "future_field": 42}), encoding="utf-8"
    )
    assert load(config_dir=tmp_path) == Config(token="tok")
