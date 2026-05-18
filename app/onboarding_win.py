"""First-launch onboarding dialog for Windows.

Mirrors the macOS NSAlert flow with a plain tkinter dialog so we do
not add another dependency (tkinter ships with the standard Python
installer and is bundled by PyInstaller for free).
"""

from __future__ import annotations

import logging
from typing import Optional

import config_store

log = logging.getLogger("anyclip.onboarding_win")


def show_onboarding() -> Optional[str]:
    """Return the chosen token or None if cancelled.

    On non-Windows hosts (used only for smoke imports) this auto-generates
    a token so the GUI path is still runnable.
    """
    try:
        import tkinter as tk
        from tkinter import messagebox, simpledialog
    except ImportError:
        log.warning("tkinter not available; auto-generating token")
        return config_store.generate_token()

    root = tk.Tk()
    root.withdraw()  # no main window, dialogs only
    try:
        choice = messagebox.askyesnocancel(
            title="Welcome to AnyClip",
            message=(
                "Choose how to set the shared clipboard token. Both "
                "devices must use the same value.\n\n"
                "Yes  = Generate new token (first device)\n"
                "No   = Enter existing token (second device)\n"
                "Cancel"
            ),
        )
        if choice is None:
            return None
        if choice:
            return config_store.generate_token()
        entered = simpledialog.askstring(
            title="Enter shared token",
            prompt="Paste the token shown on your first device:",
        )
        if entered is None:
            return None
        entered = entered.strip()
        return entered or None
    finally:
        try:
            root.destroy()
        except Exception:
            pass
