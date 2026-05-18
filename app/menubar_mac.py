"""macOS menubar (rumps) GUI shell for AnyClip.

Owned by py2app; entry point used by `setup.py`. Boots the daemon in
a background thread via `DaemonSupervisor` and reflects state changes
on the menubar icon + a few menu items. Runs the first-launch
onboarding dialog when no on-disk token is found.
"""

from __future__ import annotations

import logging
import os
import socket
import subprocess
import sys
import time
from pathlib import Path
from queue import Empty
from typing import Optional

import anyclip
import autostart
import config_store
import peer_state

ICONS_DIR = Path(__file__).resolve().parent / "icons"

# The menubar title is a single static "@" character per user request.
# All state distinction lives in the dropdown menu items (Status label,
# Last Sync, "Open Local Network Settings" appears on error_local_network)
# so the menubar stays visually quiet.
MENUBAR_TITLE = "@"

log = logging.getLogger("anyclip.menubar_mac")

LOCAL_NETWORK_SETTINGS_URL = (
    "x-apple.systempreferences:com.apple.preference.security"
    "?Privacy_LocalNetwork"
)


def _build_config(token: str) -> anyclip.Config:
    """Fill anyclip.Config with sensible defaults for GUI-launched runs."""
    return anyclip.Config(
        token=token,
        port=anyclip.DEFAULT_PORT,
        name=socket.gethostname(),
        poll_interval=0.5,
        verbose=False,
        peers=[],
        no_notify=False,
    )


def _resolve_token() -> Optional[str]:
    """CLI/env > on-disk config > onboarding dialog."""
    cli_env_token = os.environ.get("ANYCLIP_TOKEN")
    if cli_env_token:
        return cli_env_token
    stored = config_store.load()
    if stored is not None:
        return stored.token
    from app.onboarding import show_onboarding

    token = show_onboarding()
    if token:
        config_store.save(config_store.Config(token=token))
    return token


def launch_gui() -> None:
    """Entry point used by setup.py and by `anyclip.py` when GUI is up."""
    try:
        import rumps  # type: ignore[import-not-found]
    except ImportError:
        log.error("rumps not installed; cannot launch macOS menubar GUI")
        raise

    token = _resolve_token()
    if not token:
        sys.stderr.write("anyclip: onboarding cancelled, exiting\n")
        return

    from app.daemon_supervisor import DaemonSupervisor

    supervisor = DaemonSupervisor(_build_config(token))
    supervisor.start()

    app = AnyClipMenubarApp(rumps, supervisor)
    app.run()


class AnyClipMenubarApp:
    """Wraps a `rumps.App` so the rumps import stays runtime-only.

    Constructing a rumps.App at module-import time would force the
    rumps dependency on every code path -- including the CLI-only
    `--headless` daemon. By taking `rumps` as a constructor arg we
    keep the import lazy.
    """

    def __init__(self, rumps_mod, supervisor) -> None:
        self.rumps = rumps_mod
        self.supervisor = supervisor

        # No tray icon: the menubar shows a single "@" glyph and stays
        # there regardless of state. State changes are reflected in the
        # dropdown menu items only.
        self.app = rumps_mod.App(
            "AnyClip",
            icon=None,
            title=MENUBAR_TITLE,
            quit_button=None,
        )
        self.status_item = rumps_mod.MenuItem("Status: idle")
        self.last_sync_item = rumps_mod.MenuItem("Last sync: —")
        self.token_item = rumps_mod.MenuItem(
            "Token…", callback=self._show_token_info
        )
        self.start_at_login_item = rumps_mod.MenuItem(
            "Start at Login", callback=self._toggle_autostart,
        )
        self.start_at_login_item.state = (
            1 if autostart.get_backend().is_enabled() else 0
        )
        self.open_logs_item = rumps_mod.MenuItem(
            "Open Logs", callback=self._open_logs,
        )
        self.check_updates_item = rumps_mod.MenuItem(
            "Check for Updates…", callback=self._check_updates,
        )
        self.quit_item = rumps_mod.MenuItem("Quit", callback=self._quit)
        self._lan_settings_item: Optional["object"] = None

        self.app.menu = [
            self.status_item,
            self.last_sync_item,
            None,  # separator
            self.token_item,
            self.start_at_login_item,
            self.open_logs_item,
            self.check_updates_item,
            None,
            self.quit_item,
        ]

        # Start Sparkle (no-op outside the .app bundle).
        try:
            from app.updater_bridge import init_updater

            init_updater()
        except Exception:
            log.exception("updater init failed; continuing without auto-update")

        self.timer = rumps_mod.Timer(self._tick, 0.5)
        self.timer.start()

    def run(self) -> None:
        self.app.run()

    # ---- timer drain ----------------------------------------------------

    def _tick(self, _sender) -> None:
        latest: Optional[peer_state.State] = None
        try:
            while True:
                latest = self.supervisor.state_queue.get_nowait()
        except Empty:
            pass
        if latest is not None:
            self._apply_state(latest)

    def _apply_state(self, state: peer_state.State) -> None:
        kind = state.kind
        # Menubar title stays "@" regardless of state -- state info
        # lives in the dropdown menu items below.
        if kind == "linked":
            self.status_item.title = f"Linked: {state.peer_name or 'peer'}"
            self.last_sync_item.title = (
                f"Linked since: {time.strftime('%H:%M:%S')}"
            )
            self._remove_lan_settings_item()
        elif kind == "searching":
            self.status_item.title = "Searching for peer"
            self._remove_lan_settings_item()
        elif kind == "error":
            reason = state.reason or "unknown"
            self.status_item.title = f"Error: {reason}"
            if reason == "local_network":
                self._add_lan_settings_item()
            else:
                self._remove_lan_settings_item()
        else:
            self.status_item.title = "Idle"
            self._remove_lan_settings_item()

    # ---- menu actions ---------------------------------------------------

    def _show_token_info(self, _sender) -> None:
        stored = config_store.load()
        current = stored.token if stored is not None else "(no token configured)"
        path = config_store.config_path()
        # rumps.alert second/third params are button titles. Cancel
        # returns 0; OK returns 1. We use Cancel as the destructive
        # Reset button so the default Enter key lands on the safe
        # "Close" action.
        response = self.rumps.alert(
            title="AnyClip token",
            message=(
                f"Current token (select to copy):\n{current}\n\n"
                f"Stored at: {path}\n\n"
                "Reset generates a new random token, saves it, and "
                "quits AnyClip. The other device must re-onboard with "
                "the new token before sync resumes."
            ),
            ok="Close",
            cancel="Reset…",
        )
        if response == 1:
            return  # Close
        # Second confirmation -- this is destructive (peer link breaks).
        confirm = self.rumps.alert(
            title="Reset token?",
            message=(
                "This will replace the current token with a fresh one. "
                "Your other device will stop syncing until you paste "
                "the new token there."
            ),
            ok="Reset",
            cancel="Cancel",
        )
        if confirm != 1:
            return
        new_token = config_store.generate_token()
        config_store.save(config_store.Config(token=new_token))
        self.rumps.alert(
            title="Token reset",
            message=(
                "New token saved:\n"
                f"{new_token}\n\n"
                "AnyClip will now quit. Relaunch to apply, then "
                "paste this token on your other device."
            ),
        )
        self._quit(None)

    def _toggle_autostart(self, sender) -> None:
        backend = autostart.get_backend()
        if sender.state:
            backend.disable()
            sender.state = 0
        else:
            backend.enable(
                executable_path=sys.executable,
                args=[os.path.abspath(sys.argv[0]), "--headless"],
            )
            sender.state = 1

    def _open_logs(self, _sender) -> None:
        subprocess.Popen(
            ["open", "-R", str(anyclip.LOG_FILE)],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def _check_updates(self, _sender) -> None:
        try:
            from app.updater_bridge import check_for_updates, is_active

            if not is_active():
                self.rumps.alert(
                    "Updates unavailable",
                    "Auto-update is only active in the packaged .app build.",
                )
                return
            check_for_updates()
        except Exception:
            log.exception("check-for-updates failed")

    def _open_lan_settings(self, _sender) -> None:
        subprocess.Popen(
            ["open", LOCAL_NETWORK_SETTINGS_URL],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def _add_lan_settings_item(self) -> None:
        if self._lan_settings_item is not None:
            return
        item = self.rumps.MenuItem(
            "Open Local Network Settings", callback=self._open_lan_settings,
        )
        self._lan_settings_item = item
        try:
            self.app.menu.insert_before("Open Logs", item)
        except Exception:
            # Older rumps versions: fall back to append.
            self.app.menu.add(item)

    def _remove_lan_settings_item(self) -> None:
        if self._lan_settings_item is None:
            return
        try:
            del self.app.menu["Open Local Network Settings"]
        except (KeyError, Exception):
            pass
        self._lan_settings_item = None

    def _quit(self, _sender) -> None:
        try:
            self.supervisor.stop(timeout=3.0)
        except Exception:
            log.exception("supervisor stop failed")
        self.rumps.quit_application()
