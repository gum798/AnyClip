"""Bridge to Sparkle (macOS) or WinSparkle (Windows) auto-updaters.

Loaded only when AnyClip runs from a packaged bundle that ships the
respective framework/DLL alongside the executable. When neither is
present (source checkout, `python anyclip.py`) every entry point
becomes a logged no-op so the daemon still works.

macOS path: PyObjC -> Sparkle.framework -> SPUStandardUpdaterController.
Windows path: ctypes -> WinSparkle.dll -> win_sparkle_init.
Both use the same appcast URL + the same EdDSA public key (same key
pair generated in slice-10).
"""

from __future__ import annotations

import logging
import os
import sys
from pathlib import Path
from typing import Optional

log = logging.getLogger("anyclip.updater_bridge")

APPCAST_URL = "https://gum798.github.io/AnyClip/appcast.xml"
# Injected at build time via env var. Empty -> WinSparkle/Sparkle
# refuses every update because the signature has nothing to verify
# against, which is the correct fail-closed default for dev builds.
SPARKLE_PUBLIC_KEY = os.environ.get("SPARKLE_PUBLIC_KEY", "")

_updater_singleton: Optional["object"] = None
_winsparkle_dll: Optional["object"] = None


def _try_load_sparkle():
    """Return the SUUpdaterController class or None.

    PyObjC loads frameworks by name; it walks NSBundle search paths,
    which inside a py2app bundle include Contents/Frameworks. Outside
    the bundle the load fails and we return None.
    """
    try:
        import objc  # type: ignore[import-not-found]
        from Foundation import NSBundle  # type: ignore[import-not-found]
    except ImportError:
        log.debug("PyObjC not available; updater disabled")
        return None
    try:
        # Standard Sparkle bundle ID.
        bundle = NSBundle.bundleWithIdentifier_("org.sparkle-project.Sparkle")
        if bundle is None:
            log.info("Sparkle.framework not in NSBundle search path; updater disabled")
            return None
        if not bundle.load():
            log.info("Sparkle.framework failed to load; updater disabled")
            return None
        # SPUStandardUpdaterController is the documented entry point in
        # modern Sparkle (2.x). Older 1.x used SUUpdater directly; try
        # that as a fallback.
        cls = objc.lookUpClass("SPUStandardUpdaterController")
        return cls
    except Exception:
        log.exception("Sparkle lookup failed")
        return None


def _try_load_winsparkle():
    """Return a loaded WinSparkle ctypes DLL handle or None.

    PyInstaller's `datas` for the Windows build drops WinSparkle.dll
    alongside the .exe. We resolve it via `sys._MEIPASS` (PyInstaller
    runtime extraction dir) or, in source-run mode, walk a couple of
    likely paths and give up if none match.
    """
    if sys.platform != "win32":
        return None
    try:
        import ctypes
    except ImportError:
        return None
    candidates = []
    meipass = getattr(sys, "_MEIPASS", None)
    if meipass:
        candidates.append(Path(meipass) / "WinSparkle.dll")
    exe_dir = Path(sys.executable).resolve().parent
    candidates.append(exe_dir / "WinSparkle.dll")
    candidates.append(Path(__file__).resolve().parent / "WinSparkle.dll")
    for path in candidates:
        if path.exists():
            try:
                return ctypes.CDLL(str(path))
            except OSError:
                log.exception("WinSparkle.dll load failed: %s", path)
                return None
    log.info("WinSparkle.dll not found in %s; updater disabled", candidates)
    return None


def init_updater() -> None:
    """Start the platform-appropriate updater. Safe to call repeatedly."""
    global _updater_singleton, _winsparkle_dll
    if _updater_singleton is not None or _winsparkle_dll is not None:
        return

    if sys.platform == "darwin":
        cls = _try_load_sparkle()
        if cls is None:
            return
        try:
            # Constructor signature for SPUStandardUpdaterController:
            #   initWithStartingUpdater:updaterDelegate:userDriverDelegate:
            _updater_singleton = cls.alloc().initWithStartingUpdater_updaterDelegate_userDriverDelegate_(
                True, None, None,
            )
            log.info("Sparkle updater initialised (feed=%s)", APPCAST_URL)
        except Exception:
            log.exception("Sparkle init failed; updater disabled")
            _updater_singleton = None
        return

    if sys.platform == "win32":
        dll = _try_load_winsparkle()
        if dll is None:
            return
        try:
            import ctypes

            # WinSparkle's wide-string API takes UTF-16LE-encoded
            # null-terminated buffers; ctypes c_wchar_p handles that.
            dll.win_sparkle_set_appcast_url.argtypes = [ctypes.c_wchar_p]
            dll.win_sparkle_set_appcast_url(APPCAST_URL)
            if SPARKLE_PUBLIC_KEY:
                dll.win_sparkle_set_dsa_pub_pem.argtypes = [ctypes.c_char_p]
                # Newer WinSparkle exposes ed25519 directly:
                if hasattr(dll, "win_sparkle_set_ed_public_key"):
                    dll.win_sparkle_set_ed_public_key.argtypes = [ctypes.c_char_p]
                    dll.win_sparkle_set_ed_public_key(
                        SPARKLE_PUBLIC_KEY.encode("ascii")
                    )
            dll.win_sparkle_init()
            _winsparkle_dll = dll
            log.info("WinSparkle updater initialised (feed=%s)", APPCAST_URL)
        except Exception:
            log.exception("WinSparkle init failed; updater disabled")
            _winsparkle_dll = None
        return


def check_for_updates() -> None:
    """Trigger an explicit user-initiated check.

    Wired to the "Check for Updates…" menu item; harmless if no updater
    is loaded (logs and returns).
    """
    if _updater_singleton is not None:
        try:
            # SPUStandardUpdaterController exposes -checkForUpdates: as
            # the IBAction users normally bind a menu item to.
            _updater_singleton.checkForUpdates_(None)
        except Exception:
            log.exception("checkForUpdates_ call failed")
        return
    if _winsparkle_dll is not None:
        try:
            _winsparkle_dll.win_sparkle_check_update_with_ui()
        except Exception:
            log.exception("win_sparkle_check_update_with_ui call failed")
        return
    log.info("updater not active; ignoring check-for-updates request")


def shutdown() -> None:
    """Tear down WinSparkle on quit. Sparkle handles its own teardown."""
    global _winsparkle_dll
    if _winsparkle_dll is None:
        return
    try:
        _winsparkle_dll.win_sparkle_cleanup()
    except Exception:
        log.exception("win_sparkle_cleanup failed")
    _winsparkle_dll = None


def is_active() -> bool:
    return _updater_singleton is not None or _winsparkle_dll is not None
