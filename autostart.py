"""Register/unregister AnyClip to launch at user login.

macOS:   ~/Library/LaunchAgents/com.anyclip.plist + `launchctl load`
Windows: HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\Run\\AnyClip

Each backend exposes the same three operations -- `is_enabled()`,
`enable(executable_path, args=None)`, `disable()` -- so the GUI shell
(Slice 6, 7) can wire a single "Start at Login" toggle without
branching on platform.

Side effects are concentrated in the backend classes; pure helpers
(plist body builder, registry-value formatter) are unit-testable
without touching the real filesystem or registry.
"""

from __future__ import annotations

import logging
import os
import plistlib
import subprocess
import sys
from pathlib import Path
from typing import List, Optional, Protocol

log = logging.getLogger("anyclip.autostart")

PLIST_LABEL = "com.anyclip"
WINDOWS_REG_VALUE_NAME = "AnyClip"
WINDOWS_REG_RUN_PATH = r"Software\Microsoft\Windows\CurrentVersion\Run"


class Backend(Protocol):
    def is_enabled(self) -> bool: ...
    def enable(self, executable_path: str, args: Optional[List[str]] = None) -> None: ...
    def disable(self) -> None: ...


# ---- macOS launchd backend -----------------------------------------------


class MacAutostart:
    """LaunchAgent plist under ~/Library/LaunchAgents."""

    def __init__(
        self,
        home_dir: Optional[Path] = None,
        run_launchctl: bool = True,
    ) -> None:
        # `home_dir` override exists for tests; production callers leave
        # it None and we resolve to the real user's HOME.
        self.home_dir = home_dir or Path.home()
        self.run_launchctl = run_launchctl

    @property
    def plist_path(self) -> Path:
        return self.home_dir / "Library" / "LaunchAgents" / f"{PLIST_LABEL}.plist"

    def is_enabled(self) -> bool:
        return self.plist_path.exists()

    def enable(self, executable_path: str, args: Optional[List[str]] = None) -> None:
        program_args = [executable_path] + list(args or [])
        log_dir = self.home_dir / ".anyclip"
        log_dir.mkdir(parents=True, exist_ok=True)
        plist_body = {
            "Label": PLIST_LABEL,
            "ProgramArguments": program_args,
            "RunAtLoad": True,
            "KeepAlive": True,
            # launchd does not expand ~ in these paths -- absolute only.
            "StandardOutPath": str(log_dir / "launchd.stdout.log"),
            "StandardErrorPath": str(log_dir / "launchd.stderr.log"),
        }
        self.plist_path.parent.mkdir(parents=True, exist_ok=True)
        # plistlib handles all string escaping (paths with spaces, quotes,
        # unicode) safely. No manual string concat into XML.
        with open(self.plist_path, "wb") as fh:
            plistlib.dump(plist_body, fh)
        if self.run_launchctl:
            # Unload first in case we are overwriting -- launchctl
            # refuses to load a label that is already registered.
            self._launchctl(["unload", str(self.plist_path)], allow_fail=True)
            self._launchctl(["load", str(self.plist_path)], allow_fail=False)

    def disable(self) -> None:
        if self.plist_path.exists() and self.run_launchctl:
            self._launchctl(["unload", str(self.plist_path)], allow_fail=True)
        try:
            self.plist_path.unlink()
        except FileNotFoundError:
            pass

    @staticmethod
    def _launchctl(args: List[str], allow_fail: bool) -> None:
        try:
            result = subprocess.run(
                ["launchctl", *args],
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=5,
            )
        except (FileNotFoundError, subprocess.SubprocessError) as exc:
            if not allow_fail:
                log.warning(f"launchctl {args} failed: {exc}")
            return
        if result.returncode != 0 and not allow_fail:
            log.warning(
                f"launchctl {args} exit={result.returncode} "
                f"stderr={result.stderr.strip()!r}"
            )


# ---- Windows registry backend --------------------------------------------


def format_windows_command(executable_path: str, args: Optional[List[str]] = None) -> str:
    """Build the Run-key string value with proper quoting.

    Wraps the executable path in double quotes so spaces in the path
    are tolerated, and re-quotes any argument that contains a space.
    """
    parts = [f'"{executable_path}"']
    for a in args or []:
        if any(c in a for c in (" ", "\t")):
            parts.append(f'"{a}"')
        else:
            parts.append(a)
    return " ".join(parts)


class WindowsAutostart:
    """HKCU Run-key entry. Persists across reboots without admin rights."""

    def __init__(
        self,
        reg_root=None,
        sub_key: str = WINDOWS_REG_RUN_PATH,
        value_name: str = WINDOWS_REG_VALUE_NAME,
    ) -> None:
        # winreg is imported lazily so importing this module on non-Windows
        # OSes (e.g. CI's Linux runners) does not crash.
        import winreg  # type: ignore[import-not-found]

        self._winreg = winreg
        self._reg_root = reg_root if reg_root is not None else winreg.HKEY_CURRENT_USER
        self._sub_key = sub_key
        self._value_name = value_name

    def is_enabled(self) -> bool:
        try:
            with self._winreg.OpenKey(
                self._reg_root, self._sub_key, 0, self._winreg.KEY_READ,
            ) as key:
                self._winreg.QueryValueEx(key, self._value_name)
                return True
        except FileNotFoundError:
            return False
        except OSError:
            return False

    def enable(self, executable_path: str, args: Optional[List[str]] = None) -> None:
        command = format_windows_command(executable_path, args)
        # CreateKey is idempotent: returns existing key if present.
        with self._winreg.CreateKey(self._reg_root, self._sub_key) as key:
            self._winreg.SetValueEx(
                key, self._value_name, 0, self._winreg.REG_SZ, command,
            )

    def disable(self) -> None:
        try:
            with self._winreg.OpenKey(
                self._reg_root, self._sub_key, 0, self._winreg.KEY_SET_VALUE,
            ) as key:
                self._winreg.DeleteValue(key, self._value_name)
        except FileNotFoundError:
            return
        except OSError as exc:
            log.warning(f"registry DeleteValue failed: {exc}")


# ---- Noop backend (other platforms) --------------------------------------


class NoopAutostart:
    """Fallback so callers do not need to special-case unsupported OSes."""

    def is_enabled(self) -> bool:
        return False

    def enable(self, executable_path: str, args: Optional[List[str]] = None) -> None:
        raise NotImplementedError("autostart is only supported on macOS and Windows")

    def disable(self) -> None:
        return


def get_backend() -> Backend:
    """Return the backend appropriate for the current platform."""
    if sys.platform == "darwin":
        return MacAutostart()
    if sys.platform == "win32":
        return WindowsAutostart()
    return NoopAutostart()


def default_executable_path() -> str:
    """Best-effort: the command line the user is *currently* running.

    Combined `python anyclip.py` runs -> reproduces the same shell
    invocation. Once the GUI app is packaged the .app/.exe path is
    passed in explicitly by the GUI shell, so this default only matters
    for the CLI `--install-autostart` path.
    """
    return f"{sys.executable} {os.path.abspath(sys.argv[0])}"
