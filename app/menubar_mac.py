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
TRAY_DIR = ICONS_DIR / "tray"


def _tray_icon_for(kind: str) -> Optional[str]:
    """Return absolute path to the tray PDF for the given state.

    Falls back to None if the asset is missing so the GUI keeps
    running with its current title-string fallback rather than
    crashing on a packaging mistake.
    """
    name = {"linked": "linked", "searching": "searching"}.get(kind, "error")
    pdf = TRAY_DIR / f"{name}.pdf"
    return str(pdf) if pdf.exists() else None

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

        # Template image: monochrome PDF that macOS auto-inverts for
        # light/dark menubar. Falls back to the unicode glyph title if
        # the asset is missing so the menubar is never blank.
        initial_icon = _tray_icon_for("searching")
        self.app = rumps_mod.App(
            "AnyClip",
            icon=initial_icon,
            template=True,
            title=None if initial_icon else "📋",
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
        icon_path = _tray_icon_for(kind)
        if icon_path is not None:
            # rumps preserves `template` across icon swaps when the
            # App was constructed with template=True, so light/dark
            # auto-inversion stays in effect.
            self.app.icon = icon_path
            self.app.title = None
        if kind == "linked":
            if icon_path is None:
                self.app.title = "📋"
            self.status_item.title = f"Linked: {state.peer_name or 'peer'}"
            self.last_sync_item.title = (
                f"Linked since: {time.strftime('%H:%M:%S')}"
            )
            self._remove_lan_settings_item()
        elif kind == "searching":
            if icon_path is None:
                self.app.title = "📋…"
            self.status_item.title = "Searching for peer"
            self._remove_lan_settings_item()
        elif kind == "error":
            if icon_path is None:
                self.app.title = "📋⚠"
            reason = state.reason or "unknown"
            self.status_item.title = f"Error: {reason}"
            if reason == "local_network":
                self._add_lan_settings_item()
            else:
                self._remove_lan_settings_item()
        else:
            if icon_path is None:
                self.app.title = "📋"
            self.status_item.title = "Idle"
            self._remove_lan_settings_item()

    # ---- menu actions ---------------------------------------------------

    def _show_token_info(self, _sender) -> None:
        path = config_store.config_path()
        self.rumps.alert(
            title="AnyClip token",
            message=(
                "The shared token lives in:\n"
                f"{path}\n\n"
                "To reset it, quit AnyClip, delete that file, and reopen."
            ),
        )

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
