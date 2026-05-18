"""PyObjC bridge to Sparkle (macOS auto-updater).

Loaded only when AnyClip runs from the py2app .app bundle that ships
Sparkle.framework under Contents/Frameworks/. When the framework is
not present (source checkout, `python anyclip.py`) every entry point
becomes a logged no-op so the daemon still works.

Slice 11 wires the parallel WinSparkle path; this module is macOS only.
"""

from __future__ import annotations

import logging
from typing import Optional

log = logging.getLogger("anyclip.updater_bridge")

APPCAST_URL = "https://gum798.github.io/AnyClip/appcast.xml"

_updater_singleton: Optional["object"] = None


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


def init_updater() -> None:
    """Start Sparkle if available. Safe to call repeatedly."""
    global _updater_singleton
    if _updater_singleton is not None:
        return
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


def check_for_updates() -> None:
    """Trigger an explicit user-initiated check.

    Wired to the "Check for Updates…" menu item; harmless if Sparkle is
    not loaded (logs and returns).
    """
    if _updater_singleton is None:
        log.info("updater not active; ignoring check-for-updates request")
        return
    try:
        # SPUStandardUpdaterController exposes -checkForUpdates: as the
        # IBAction users normally bind a menu item to.
        _updater_singleton.checkForUpdates_(None)
    except Exception:
        log.exception("checkForUpdates_ call failed")


def is_active() -> bool:
    return _updater_singleton is not None
