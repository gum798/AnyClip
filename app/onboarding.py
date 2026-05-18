"""First-launch onboarding dialog for macOS.

Shown when ~/.anyclip/config.json is missing. Returns the chosen token
(either freshly generated or pasted from the first device), or None if
the user cancelled. PyObjC NSAlert keeps the dependency surface to
what py2app already bundles -- no Tk, no extra wheels.
"""

from __future__ import annotations

import logging
from typing import Optional

import config_store

log = logging.getLogger("anyclip.onboarding")


def show_onboarding() -> Optional[str]:
    """Return the chosen token or None if cancelled.

    On non-macOS hosts this falls back to generating a token silently
    so the GUI path stays runnable for smoke tests off-platform.
    """
    try:
        from AppKit import (  # type: ignore[import-not-found]
            NSAlert,
            NSMakeRect,
            NSTextField,
        )
    except ImportError:
        log.warning("PyObjC not available; auto-generating token")
        return config_store.generate_token()

    alert = NSAlert.alloc().init()
    alert.setMessageText_("Welcome to AnyClip")
    alert.setInformativeText_(
        "Choose how to set the shared clipboard token. Both devices "
        "must use the same value."
    )
    alert.addButtonWithTitle_("Generate new token (first device)")
    alert.addButtonWithTitle_("Enter existing token (second device)")
    alert.addButtonWithTitle_("Cancel")

    response = alert.runModal()
    # NSAlertFirstButtonReturn = 1000, then 1001, 1002, ...
    if response == 1000:
        return config_store.generate_token()
    if response == 1001:
        return _prompt_for_token(NSAlert, NSTextField, NSMakeRect)
    return None


def _prompt_for_token(NSAlert, NSTextField, NSMakeRect) -> Optional[str]:
    alert = NSAlert.alloc().init()
    alert.setMessageText_("Enter shared token")
    alert.setInformativeText_(
        "Paste the token shown on your first device."
    )
    alert.addButtonWithTitle_("OK")
    alert.addButtonWithTitle_("Cancel")

    field = NSTextField.alloc().initWithFrame_(NSMakeRect(0, 0, 320, 24))
    alert.setAccessoryView_(field)
    response = alert.runModal()
    if response != 1000:
        return None
    value = field.stringValue()
    return str(value).strip() or None
