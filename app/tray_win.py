"""Windows system-tray (pystray) GUI shell for AnyClip.

Same user-visible model as the macOS menubar shell: a daemon
supervisor runs `anyclip.py` in a background thread, the GUI polls
the state queue and updates icon + tooltip + menu items. Onboarding
uses a tkinter dialog rather than NSAlert.

Permission_probe is a no-op on Windows (Windows has no Local Network
concept; the firewall is handled by the OS popup on first listen).
"""

from __future__ import annotations

import logging
import os
import socket
import subprocess
import sys
import threading
import time
from queue import Empty
from typing import Optional

import anyclip
import autostart
import config_store
import peer_state

log = logging.getLogger("anyclip.tray_win")


def _build_config(token: str) -> anyclip.Config:
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
    env_token = os.environ.get("ANYCLIP_TOKEN")
    if env_token:
        return env_token
    stored = config_store.load()
    if stored is not None:
        return stored.token
    from app.onboarding_win import show_onboarding

    token = show_onboarding()
    if token:
        config_store.save(config_store.Config(token=token))
    return token


def launch_gui() -> None:
    """Entry point used by `anyclip.py` on Windows."""
    try:
        import pystray  # type: ignore[import-not-found]
        from PIL import Image, ImageDraw
    except ImportError:
        log.error("pystray/Pillow not installed; cannot launch Windows tray GUI")
        raise

    token = _resolve_token()
    if not token:
        sys.stderr.write("anyclip: onboarding cancelled, exiting\n")
        return

    from app.daemon_supervisor import DaemonSupervisor

    supervisor = DaemonSupervisor(_build_config(token))
    supervisor.start()

    tray = AnyClipTrayApp(pystray, Image, ImageDraw, supervisor)
    tray.run()


def _make_status_icon(Image, ImageDraw, kind: str):
    """Render a 64x64 placeholder icon coloured per state.

    Slice 8 swaps these for real assets. Until then the colour is
    enough to tell the three states apart in the tray.
    """
    colour = {
        "linked": (52, 168, 83, 255),     # green
        "searching": (251, 188, 5, 255),  # amber
        "error": (234, 67, 53, 255),      # red
        "idle": (154, 160, 166, 255),     # grey
    }.get(kind, (154, 160, 166, 255))
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse((6, 6, 58, 58), fill=colour)
    return img


class AnyClipTrayApp:
    """Wraps pystray.Icon. Same role as menubar_mac.AnyClipMenubarApp."""

    def __init__(self, pystray_mod, Image, ImageDraw, supervisor) -> None:
        self.pystray = pystray_mod
        self.Image = Image
        self.ImageDraw = ImageDraw
        self.supervisor = supervisor
        self._current_state = peer_state.INITIAL

        self.icon = pystray_mod.Icon(
            "AnyClip",
            icon=_make_status_icon(Image, ImageDraw, "idle"),
            title="AnyClip — idle",
            menu=self._build_menu(),
        )

        # pystray's update loop runs inside icon.run(); we tick from a
        # daemon thread so the queue is drained even before the user
        # opens the menu.
        self._stop = threading.Event()
        self._ticker = threading.Thread(
            target=self._tick_loop, daemon=True, name="anyclip-tray-tick",
        )

    def run(self) -> None:
        self._ticker.start()
        try:
            self.icon.run()
        finally:
            self._stop.set()

    # ---- menu ------------------------------------------------------------

    def _build_menu(self):
        pm = self.pystray.Menu
        mi = self.pystray.MenuItem

        return pm(
            mi(lambda item: self._status_label(), None, enabled=False),
            mi(lambda item: self._last_sync_label(), None, enabled=False),
            pm.SEPARATOR,
            mi("Token…", self._on_token),
            mi(
                "Start at Login",
                self._on_toggle_autostart,
                checked=lambda item: autostart.get_backend().is_enabled(),
            ),
            mi("Open Logs", self._on_open_logs),
            pm.SEPARATOR,
            mi("Quit", self._on_quit),
        )

    def _status_label(self) -> str:
        s = self._current_state
        if s.kind == "linked":
            return f"Linked: {s.peer_name or 'peer'}"
        if s.kind == "searching":
            return "Searching for peer"
        if s.kind == "error":
            return f"Error: {s.reason or 'unknown'}"
        return "Idle"

    def _last_sync_label(self) -> str:
        s = self._current_state
        if s.kind == "linked" and s.since is not None:
            return f"Linked since: {time.strftime('%H:%M:%S')}"
        return "Last sync: —"

    # ---- ticker ----------------------------------------------------------

    def _tick_loop(self) -> None:
        while not self._stop.is_set():
            latest: Optional[peer_state.State] = None
            try:
                while True:
                    latest = self.supervisor.state_queue.get_nowait()
            except Empty:
                pass
            if latest is not None:
                self._apply_state(latest)
            self._stop.wait(0.5)

    def _apply_state(self, state: peer_state.State) -> None:
        self._current_state = state
        self.icon.icon = _make_status_icon(self.Image, self.ImageDraw, state.kind)
        title = self._status_label()
        try:
            self.icon.title = f"AnyClip — {title}"
        except Exception:
            # Some pystray backends ignore late title updates; not fatal.
            pass
        try:
            self.icon.update_menu()
        except Exception:
            pass

    # ---- menu actions ---------------------------------------------------

    def _on_token(self, _icon, _item) -> None:
        path = config_store.config_path()
        try:
            from tkinter import messagebox

            messagebox.showinfo(
                "AnyClip token",
                f"The shared token lives in:\n{path}\n\n"
                "To reset it, quit AnyClip, delete that file, and reopen.",
            )
        except Exception:
            log.info(f"AnyClip token stored at {path}")

    def _on_toggle_autostart(self, _icon, item) -> None:
        backend = autostart.get_backend()
        if backend.is_enabled():
            backend.disable()
        else:
            backend.enable(
                executable_path=sys.executable,
                args=[os.path.abspath(sys.argv[0]), "--headless"],
            )
        try:
            self.icon.update_menu()
        except Exception:
            pass

    def _on_open_logs(self, _icon, _item) -> None:
        # Explorer's /select highlights the file in its containing folder.
        # Quote the path to tolerate spaces in the user's home dir.
        subprocess.Popen(
            ["explorer", f"/select,{anyclip.LOG_FILE}"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
        )

    def _on_quit(self, _icon, _item) -> None:
        try:
            self.supervisor.stop(timeout=3.0)
        except Exception:
            log.exception("supervisor stop failed")
        self.icon.stop()
