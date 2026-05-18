"""Tests for the autostart deep module.

macOS: uses a temp HOME and bypasses launchctl so we exercise only the
filesystem half. Windows: skipped unless running on win32, and even
then uses a throwaway subkey under HKCU so the user's real Run key is
never touched.
"""

from __future__ import annotations

import plistlib
import sys
from pathlib import Path

import pytest

from autostart import (
    MacAutostart,
    PLIST_LABEL,
    format_windows_command,
)


# ---- format_windows_command (pure, runs everywhere) ----------------------


def test_format_windows_command_simple() -> None:
    cmd = format_windows_command(r"C:\Program Files\AnyClip\AnyClip.exe")
    assert cmd == r'"C:\Program Files\AnyClip\AnyClip.exe"'


def test_format_windows_command_with_args() -> None:
    cmd = format_windows_command(
        r"C:\Program Files\AnyClip\AnyClip.exe",
        ["--headless"],
    )
    assert cmd == r'"C:\Program Files\AnyClip\AnyClip.exe" --headless'


def test_format_windows_command_quotes_arg_with_space() -> None:
    cmd = format_windows_command(r"C:\bin\app.exe", ["--name", "my name"])
    assert cmd == r'"C:\bin\app.exe" --name "my name"'


# ---- macOS backend (skipped on non-macOS hosts) --------------------------

darwin_only = pytest.mark.skipif(
    sys.platform != "darwin",
    reason="macOS launchd backend only meaningful on macOS",
)


def _mac_no_launchctl(tmp_path: Path) -> MacAutostart:
    return MacAutostart(home_dir=tmp_path, run_launchctl=False)


@darwin_only
def test_mac_enable_then_disable_roundtrip(tmp_path: Path) -> None:
    a = _mac_no_launchctl(tmp_path)
    assert not a.is_enabled()
    a.enable("/Applications/AnyClip.app/Contents/MacOS/AnyClip", ["--headless"])
    assert a.is_enabled()
    plist = tmp_path / "Library" / "LaunchAgents" / f"{PLIST_LABEL}.plist"
    assert plist.exists()
    a.disable()
    assert not a.is_enabled()
    assert not plist.exists()


@darwin_only
def test_mac_enable_is_idempotent(tmp_path: Path) -> None:
    a = _mac_no_launchctl(tmp_path)
    a.enable("/path/to/exe", ["--headless"])
    a.enable("/path/to/exe", ["--headless"])
    assert a.is_enabled()


@darwin_only
def test_mac_plist_contents(tmp_path: Path) -> None:
    a = _mac_no_launchctl(tmp_path)
    a.enable("/Applications/AnyClip.app/Contents/MacOS/AnyClip", ["--headless"])
    with open(a.plist_path, "rb") as fh:
        body = plistlib.load(fh)
    assert body["Label"] == PLIST_LABEL
    assert body["RunAtLoad"] is True
    assert body["KeepAlive"] is True
    assert body["ProgramArguments"] == [
        "/Applications/AnyClip.app/Contents/MacOS/AnyClip",
        "--headless",
    ]
    assert body["StandardOutPath"].endswith(".anyclip/launchd.stdout.log")
    assert body["StandardErrorPath"].endswith(".anyclip/launchd.stderr.log")


@darwin_only
def test_mac_plist_escapes_special_chars(tmp_path: Path) -> None:
    """plistlib must handle paths containing spaces, quotes, unicode
    without manual string escaping breaking the XML."""
    a = _mac_no_launchctl(tmp_path)
    weird = '/Applications/My "AnyClip" 한글/Contents/MacOS/AnyClip'
    a.enable(weird, ["--name", "host with space"])
    with open(a.plist_path, "rb") as fh:
        body = plistlib.load(fh)
    assert body["ProgramArguments"] == [weird, "--name", "host with space"]


@darwin_only
def test_mac_disable_when_not_enabled_is_noop(tmp_path: Path) -> None:
    a = _mac_no_launchctl(tmp_path)
    a.disable()  # must not raise
    assert not a.is_enabled()


# ---- Windows backend (skipped unless running on Windows) -----------------

windows_only = pytest.mark.skipif(
    sys.platform != "win32",
    reason="Windows registry backend only meaningful on Windows",
)


@windows_only
def test_windows_enable_disable_roundtrip() -> None:
    import winreg

    from autostart import WindowsAutostart

    test_subkey = r"Software\AnyClipTest\Run"
    test_value = "AnyClipPyTest"
    a = WindowsAutostart(sub_key=test_subkey, value_name=test_value)
    a.disable()  # ensure clean slate
    assert not a.is_enabled()
    a.enable(r"C:\bin\AnyClip.exe", ["--headless"])
    assert a.is_enabled()
    # idempotent
    a.enable(r"C:\bin\AnyClip.exe", ["--headless"])
    assert a.is_enabled()
    a.disable()
    assert not a.is_enabled()
    # clean up the parent test subkey to avoid leftover
    try:
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, test_subkey)
        winreg.DeleteKey(winreg.HKEY_CURRENT_USER, r"Software\AnyClipTest")
    except OSError:
        pass
