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
from pathlib import Path
from queue import Empty
from typing import Optional

import anyclip
import autostart
import config_store
import peer_state

ICONS_DIR = Path(__file__).resolve().parent / "icons"
APP_ICON_ICO = ICONS_DIR / "anyclip.ico"

# Win32 MessageBoxW button flags.
_MB_OK = 0x0
_MB_YESNO = 0x4
_MB_ICONINFORMATION = 0x40
_MB_ICONQUESTION = 0x20
_MB_ICONWARNING = 0x30
# Force the message box to the top + bring it to the foreground so a
# pystray-thread dialog actually receives input focus. Without these
# flags the box can render but the OS may keep input focus on the
# Explorer notification area, so button clicks land on whatever was
# behind the dialog.
_MB_TOPMOST = 0x40000
_MB_SETFOREGROUND = 0x10000
_MB_SYSTEMMODAL = 0x1000
_DIALOG_FLAGS = _MB_TOPMOST | _MB_SETFOREGROUND | _MB_SYSTEMMODAL
# MessageBoxW return values.
_IDOK = 1
_IDYES = 6
_IDNO = 7


def _native_yesno(title: str, text: str) -> bool:
    """Win32 MessageBox with Yes/No buttons. Returns True for Yes.

    Used instead of tkinter.messagebox because pystray invokes menu
    callbacks on a worker thread, and tkinter modal dialogs only
    work from the main thread on Windows. MessageBoxW is a synchronous
    native API and is safe to call from any thread.
    """
    try:
        import ctypes

        result = ctypes.windll.user32.MessageBoxW(
            0, text, title,
            _MB_YESNO | _MB_ICONQUESTION | _DIALOG_FLAGS,
        )
        return result == _IDYES
    except Exception:
        log.exception("MessageBoxW failed; treating as No")
        return False


def _native_info(title: str, text: str) -> None:
    """Win32 MessageBox with a single OK button."""
    try:
        import ctypes

        ctypes.windll.user32.MessageBoxW(
            0, text, title,
            _MB_OK | _MB_ICONINFORMATION | _DIALOG_FLAGS,
        )
    except Exception:
        log.exception("MessageBoxW (info) failed")

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


def _load_app_icon(Image, ImageDraw):
    """Return the single AnyClip tray image (state-independent).

    Matches the macOS menubar's "show one icon always" UX. State info
    lives in the tray tooltip + menu items instead of state-coloured
    icons. Falls back to a coloured disc if the bundled .ico cannot
    be read so the tray is never empty.
    """
    if APP_ICON_ICO.exists():
        try:
            return Image.open(APP_ICON_ICO).convert("RGBA")
        except Exception:
            log.exception("app icon load failed: %s", APP_ICON_ICO)
    # Last-resort fallback so a packaging mistake never leaves the
    # tray empty.
    img = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.ellipse((6, 6, 58, 58), fill=(26, 115, 232, 255))
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
            icon=_load_app_icon(Image, ImageDraw),
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
            from app.updater_bridge import init_updater

            init_updater()
        except Exception:
            log.exception("updater init failed; continuing without auto-update")
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
            mi("Check for Updates…", self._on_check_updates),
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
        # Tray icon is constant (single AnyClip glyph); state info
        # lives in the tooltip + menu items only.
        self._current_state = state
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
        stored = config_store.load()
        current = stored.token if stored is not None else "(no token configured)"
        path = config_store.config_path()
        # pystray invokes menu callbacks on a worker thread; use Win32
        # MessageBoxW directly so the dialog is responsive (tkinter
        # would freeze).
        do_reset = _native_yesno(
            "AnyClip token",
            f"Current token:\n{current}\n\n"
            f"Stored at: {path}\n\n"
            "Press Yes to reset and generate a new token "
            "(your other device will stop syncing until you "
            "paste the new token there).",
        )
        if not do_reset:
            return
        confirm = _native_yesno(
            "Reset token?",
            "This will replace the current token. Your other "
            "device will stop syncing until you paste the new "
            "token there. Proceed?",
        )
        if not confirm:
            return
        new_token = config_store.generate_token()
        config_store.save(config_store.Config(token=new_token))
        _native_info(
            "Token reset",
            f"New token saved:\n{new_token}\n\n"
            "AnyClip will now quit. Relaunch to apply, then "
            "paste this token on your other device.",
        )
        self._on_quit(_icon, _item)

    def _on_toggle_autostart(self, _icon, item) -> None:
        backend = autostart.get_backend()
        if backend.is_enabled():
            backend.disable()
        else:
            exe, extra = autostart.default_launch_command()
            backend.enable(executable_path=exe, args=extra)
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

    def _on_check_updates(self, _icon, _item) -> None:
        try:
            from app.updater_bridge import check_for_updates, is_active

            if not is_active():
                _native_info(
                    "Updates unavailable",
                    "Auto-update is only active in the packaged "
                    ".exe build.",
                )
                return
            check_for_updates()
        except Exception:
            log.exception("check-for-updates failed")

    def _on_quit(self, _icon, _item) -> None:
        try:
            self.supervisor.stop(timeout=3.0)
        except Exception:
            log.exception("supervisor stop failed")
        try:
            from app.updater_bridge import shutdown as updater_shutdown

            updater_shutdown()
        except Exception:
            pass
        self.icon.stop()
