#!/usr/bin/env python3
"""AnyClip - simple cross-device clipboard sharing over LAN.

Phase 1 scope: Windows <-> macOS, text only, mDNS auto-discovery, shared token.
"""
from __future__ import annotations

import argparse
import asyncio
import base64
import errno
import hashlib
import io
import json
import logging
import os
import signal
import socket
import subprocess
import sys
import tempfile
import time
import uuid
from dataclasses import dataclass
from logging.handlers import RotatingFileHandler
from pathlib import Path
from typing import Optional

import pyperclip
from zeroconf import IPVersion, ServiceInfo, ServiceStateChange
from zeroconf.asyncio import AsyncServiceBrowser, AsyncZeroconf

import autostart
import config_store
import peer_state
import permission_probe
from peer_state import (
    DaemonEvent,
    HandshakeFailed,
    LinkDown,
    LinkUp,
    PeerDiscovered,
    PermissionMissing,
)
from version_negotiator import (
    Compatibility,
    VersionInfo,
    link_allowed,
    negotiate,
)

try:
    from PIL import Image, ImageGrab
    _PIL_OK = True
except Exception:
    Image = None  # type: ignore[assignment]
    ImageGrab = None  # type: ignore[assignment]
    _PIL_OK = False

SERVICE_TYPE = "_anyclip._tcp.local."
# Single source of truth for the app build. Injected into handshake JSON
# and mDNS TXT so peers can show "peer needs update" hints. Bump in lockstep
# with releases. PROTOCOL_MAJOR/MINOR are independent: PROTOCOL_MAJOR is the
# wire-compat key (mismatch = refuse link), PROTOCOL_MINOR is informational.
# CI exports ANYCLIP_BUILD_VERSION from the git tag (without leading "v");
# local source runs default to a dev marker so handshake logs stay readable.
APP_VERSION = os.environ.get("ANYCLIP_BUILD_VERSION", "0.0.0-dev")
PROTOCOL_MAJOR = 1
PROTOCOL_MINOR = 0
# Legacy alias: pre-1.0 peers send a single `version` int. New code treats
# it as equivalent to protocol_major so old<->new handshakes still link.
PROTOCOL_VERSION = PROTOCOL_MAJOR
MAX_PAYLOAD = 16 * 1024 * 1024  # 16 MiB hard cap per frame (enough for typical PNGs)
DEFAULT_PORT = 24816
HANDSHAKE_TIMEOUT = 5.0
CONNECT_TIMEOUT = 5.0
# After a link is registered, only late-arriving handshakes within this
# window are eligible to *replace* the existing link via the node_id
# tie-breaker. Anything later is treated as a stale duplicate and dropped
# untouched, so an established link stops flapping when both sides keep
# retrying (mDNS rediscovery, peer_keepalive, Windows reconnect, etc).
RACE_WINDOW_S = 1.5
# After this many consecutive failed outbound attempts to the same
# (host, port) the address is pruned from known_peers. mDNS rediscovery
# re-adds it automatically. Keeps the daemon from poking forever at a
# stale IP after the peer DHCP-renewed onto a different address.
MAX_RECONNECT_FAILS = 3

log = logging.getLogger("anyclip")

LOG_DIR = Path.home() / ".anyclip"
LOG_FILE = LOG_DIR / "anyclip.log"
LOG_MAX_BYTES = 5 * 1024 * 1024
LOG_BACKUP_COUNT = 3
PID_FILE = LOG_DIR / "anyclip.pid"

# UI shell subscribes to this queue to drive the menubar/tray state
# machine (see peer_state.reduce). In headless mode no one reads from
# the queue; emit_event() drops on full so the daemon never blocks or
# leaks memory even after running for days without a subscriber.
EVENT_QUEUE_MAX = 256
_event_bus: Optional["asyncio.Queue[DaemonEvent]"] = None


def init_event_bus() -> "asyncio.Queue[DaemonEvent]":
    """Create the daemon-event queue. Call once from run() after the
    asyncio loop has started so the queue is bound to the running loop.
    """
    global _event_bus
    _event_bus = asyncio.Queue(maxsize=EVENT_QUEUE_MAX)
    return _event_bus


def emit_event(event: DaemonEvent) -> None:
    """Best-effort fire-and-forget event publish.

    Drops silently if the bus is not initialised (headless tests) or
    full (no subscriber draining). This keeps emit sites free of
    error handling and means the daemon stays correct even if the GUI
    shell crashes and stops consuming.
    """
    bus = _event_bus
    if bus is None:
        return
    try:
        bus.put_nowait(event)
    except asyncio.QueueFull:
        # Lossy drop: prefer to drop the new event so a stalled
        # subscriber still sees the older history rather than nothing.
        log.debug("event queue full, dropping %s", type(event).__name__)


class FatalStartupError(RuntimeError):
    """Raised when the daemon cannot start and retrying will not help.

    The supervisor in main() recognises this and exits with the message
    instead of looping with backoff.
    """


def _process_alive(pid: int) -> bool:
    """True if the OS reports a process with this pid (best-effort)."""
    if pid <= 0:
        return False
    try:
        os.kill(pid, 0)
        return True
    except ProcessLookupError:
        return False
    except PermissionError:
        # Process exists but is owned by another user.
        return True
    except OSError:
        return False


def _is_anyclip_pid(pid: int) -> bool:
    """Best-effort: does this pid look like an anyclip.py process?

    Used as a guard before we kill anything: we never terminate a process
    that is not clearly one of our own daemons. On Windows we cannot probe
    this cheaply without psutil, so we trust the PID-file evidence alone.
    """
    if sys.platform == "win32":
        return True
    try:
        out = subprocess.check_output(
            ["ps", "-p", str(pid), "-o", "args="],
            stderr=subprocess.DEVNULL, text=True, timeout=2,
        )
    except (subprocess.SubprocessError, OSError):
        return False
    return "anyclip" in out


def _find_listening_pid(port: int) -> Optional[int]:
    """Best-effort: pid LISTENing on tcp `port`. None on Windows / not found."""
    if sys.platform == "win32":
        return None
    try:
        out = subprocess.check_output(
            ["lsof", "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"],
            stderr=subprocess.DEVNULL, text=True, timeout=2,
        )
    except (subprocess.SubprocessError, OSError):
        return None
    for line in out.splitlines():
        line = line.strip()
        if line.isdigit():
            return int(line)
    return None


def _terminate_pid(pid: int) -> bool:
    """SIGTERM, wait up to 2s, then SIGKILL on POSIX. Returns True if pid is gone."""
    try:
        os.kill(pid, signal.SIGTERM)
    except ProcessLookupError:
        return True
    except OSError:
        return not _process_alive(pid)
    for _ in range(20):
        time.sleep(0.1)
        if not _process_alive(pid):
            return True
    if sys.platform != "win32":
        try:
            os.kill(pid, signal.SIGKILL)
        except OSError:
            pass
        for _ in range(10):
            time.sleep(0.1)
            if not _process_alive(pid):
                return True
    return not _process_alive(pid)


def prepare_pid_lock(port: int) -> None:
    """Ensure we are the only anyclip.

    If a previous anyclip is detected (via PID file or via ``lsof`` on the
    listening port) we terminate it and continue. Foreign processes that
    happen to hold the port are NEVER killed -- we raise FatalStartupError
    so the user can decide.
    """
    LOG_DIR.mkdir(parents=True, exist_ok=True)

    # 1) PID file from a previous run.
    if PID_FILE.exists():
        old_pid = 0
        try:
            content = PID_FILE.read_text().strip().split()
            if content:
                old_pid = int(content[0])
        except (OSError, ValueError):
            old_pid = 0
        if old_pid and old_pid != os.getpid() and _process_alive(old_pid):
            log.info(f"another anyclip detected (pid {old_pid} via PID file); terminating")
            if not _terminate_pid(old_pid):
                raise FatalStartupError(
                    f"could not terminate previous anyclip (pid {old_pid}); "
                    f"please run: kill -9 {old_pid}"
                )
            log.info(f"previous anyclip (pid {old_pid}) terminated")

    # 2) Stale state: PID file missing or already-cleared, but the port is held.
    listener_pid = _find_listening_pid(port)
    if listener_pid and listener_pid != os.getpid():
        if _is_anyclip_pid(listener_pid):
            log.info(
                f"anyclip listening on tcp/{port} (pid {listener_pid}); terminating"
            )
            if not _terminate_pid(listener_pid):
                raise FatalStartupError(
                    f"could not terminate anyclip on tcp/{port} (pid {listener_pid}); "
                    f"please run: kill -9 {listener_pid}"
                )
            # Give the OS a moment to release the socket so our bind() succeeds.
            time.sleep(0.3)
        else:
            raise FatalStartupError(
                f"tcp/{port} is held by a non-anyclip process (pid {listener_pid}); "
                f"stop that process or pick a different --port"
            )

    # 3) Record our pid (and chosen port for diagnostics).
    try:
        PID_FILE.write_text(f"{os.getpid()} {port}\n")
    except OSError as exc:
        log.warning(f"could not write PID file {PID_FILE}: {exc}")


def release_pid_lock() -> None:
    """Remove our PID file, but only if it still points at us."""
    try:
        if not PID_FILE.exists():
            return
        content = PID_FILE.read_text().strip().split()
        if content and int(content[0]) == os.getpid():
            PID_FILE.unlink()
    except (OSError, ValueError):
        pass


def clear_received_dir() -> None:
    """Empty ``~/.anyclip/received/`` of any inbound clipboard files.

    Called on startup (post-PID-lock) and on graceful shutdown so disk
    use does not grow unbounded across restarts. A SIGKILL skips the
    finally cleanup; the next startup picks up the slack.
    """
    target = LOG_DIR / "received"
    if not target.exists():
        return
    for entry in target.iterdir():
        try:
            if entry.is_file() or entry.is_symlink():
                entry.unlink()
        except OSError as exc:
            log.debug(f"could not remove {entry}: {exc}")


def setup_logging(verbose: bool) -> None:
    """Configure root logger with a rotating file handler + console handler.

    Idempotent: removes existing handlers first so supervisor restarts do
    not stack duplicates. File handler is always DEBUG; console respects
    --verbose.
    """
    fmt = "%(asctime)s %(levelname)s %(message)s"
    formatter = logging.Formatter(fmt)
    root = logging.getLogger()
    for handler in list(root.handlers):
        root.removeHandler(handler)
    root.setLevel(logging.DEBUG)
    # Silence third-party DEBUG noise that would otherwise drown our own
    # output in --verbose mode (PIL chunk parsing fires on every poll;
    # zeroconf is chatty on the cache layer; asyncio prints selector picks).
    for noisy in ("PIL", "PIL.PngImagePlugin", "PIL.Image",
                  "zeroconf", "asyncio"):
        logging.getLogger(noisy).setLevel(logging.INFO)

    console = logging.StreamHandler(sys.stderr)
    console.setLevel(logging.DEBUG if verbose else logging.INFO)
    console.setFormatter(formatter)
    root.addHandler(console)

    try:
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        file_handler = RotatingFileHandler(
            str(LOG_FILE),
            maxBytes=LOG_MAX_BYTES,
            backupCount=LOG_BACKUP_COUNT,
            encoding="utf-8",
        )
        file_handler.setLevel(logging.DEBUG)
        file_handler.setFormatter(formatter)
        root.addHandler(file_handler)
    except OSError as exc:
        log.warning(f"file logging disabled: {exc}")


def sha256_hex(data: str) -> str:
    return hashlib.sha256(data.encode("utf-8")).hexdigest()


def sha256_bytes(data: bytes) -> str:
    return hashlib.sha256(data).hexdigest()


def grab_clipboard_image() -> Optional[bytes]:
    """Return PNG bytes of an image currently on the clipboard, or None.

    macOS / Windows only. PIL.ImageGrab returns None if the clipboard does
    not hold a bitmap. Any backend exception is swallowed -- we just treat
    that as 'no image right now'.
    """
    if not _PIL_OK or ImageGrab is None:
        return None
    try:
        result = ImageGrab.grabclipboard()
    except Exception:
        return None
    if result is None:
        return None
    # On some platforms ImageGrab returns a list of file paths (when the
    # clipboard holds file references rather than a bitmap). We ignore
    # those -- we only sync inline images.
    if isinstance(result, list):
        return None
    if not hasattr(result, "save"):
        return None
    try:
        buf = io.BytesIO()
        result.save(buf, format="PNG")
        return buf.getvalue()
    except Exception:
        return None


def grab_clipboard_files() -> list:
    """Return absolute file paths currently on the clipboard, or [].

    macOS  -> osascript: ``the clipboard as «class furl»`` (single item).
    Windows-> PowerShell: ``Get-Clipboard -Format FileDropList``.
    Other  -> always [].
    """
    if sys.platform == "darwin":
        try:
            result = subprocess.run(
                ["osascript", "-e",
                 'try\n'
                 '\treturn POSIX path of (the clipboard as «class furl»)\n'
                 'on error\n'
                 '\treturn ""\n'
                 'end try'],
                capture_output=True, text=True, timeout=3,
            )
            path = result.stdout.strip()
            return [path] if path else []
        except Exception:
            return []
    if sys.platform == "win32":
        try:
            result = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 "Get-Clipboard -Format FileDropList | "
                 "ForEach-Object { $_.FullName }"],
                capture_output=True, text=True, timeout=3,
                creationflags=0x08000000,
            )
            return [line.strip() for line in result.stdout.splitlines()
                    if line.strip()]
        except Exception:
            return []
    return []


def set_clipboard_file(path: str) -> bool:
    """Place a single file reference on the system clipboard.

    macOS  -> AppleScript ``POSIX file``.
    Windows-> PowerShell ``Set-Clipboard -Path``.
    Other  -> no-op (False).
    """
    abs_path = str(Path(path).resolve())
    if sys.platform == "darwin":
        try:
            esc = abs_path.replace("\\", "\\\\").replace('"', '\\"')
            script = f'set the clipboard to (POSIX file "{esc}")'
            result = subprocess.run(
                ["osascript", "-e", script],
                capture_output=True, timeout=5,
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_file (macOS) failed: {exc}")
            return False
    if sys.platform == "win32":
        try:
            esc = abs_path.replace("'", "''")
            ps = f"Set-Clipboard -Path '{esc}'"
            result = subprocess.run(
                ["powershell", "-NoProfile", "-Command", ps],
                capture_output=True, timeout=5,
                creationflags=0x08000000,
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_file (Windows) failed: {exc}")
            return False
    return False


def set_clipboard_image(png_bytes: bytes) -> bool:
    """Best-effort: place a PNG image on the system clipboard.

    macOS: AppleScript reads a temp file in as ``«class PNGf»``.
    Windows: PowerShell + System.Windows.Forms.Clipboard.SetImage.
    Other platforms: no-op (returns False).
    """
    if sys.platform == "darwin":
        path: Optional[str] = None
        try:
            with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as fh:
                fh.write(png_bytes)
                path = fh.name
            script = (
                f'set the clipboard to '
                f'(read POSIX file "{path}" as «class PNGf»)'
            )
            result = subprocess.run(
                ["osascript", "-e", script],
                stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                timeout=5,
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_image (macOS) failed: {exc}")
            return False
        finally:
            if path:
                try:
                    os.unlink(path)
                except OSError:
                    pass
    if sys.platform == "win32":
        path = None
        try:
            with tempfile.NamedTemporaryFile(suffix=".png", delete=False) as fh:
                fh.write(png_bytes)
                path = fh.name
            ps_path = path.replace("'", "''")
            ps = (
                "Add-Type -AssemblyName System.Windows.Forms;"
                "Add-Type -AssemblyName System.Drawing;"
                f"$img = [System.Drawing.Image]::FromFile('{ps_path}');"
                "[System.Windows.Forms.Clipboard]::SetImage($img);"
                "$img.Dispose()"
            )
            result = subprocess.run(
                ["powershell", "-NoProfile", "-STA", "-WindowStyle", "Hidden",
                 "-Command", ps],
                stdout=subprocess.DEVNULL, stderr=subprocess.PIPE,
                timeout=8,
                creationflags=0x08000000,  # CREATE_NO_WINDOW
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_image (Windows) failed: {exc}")
            return False
        finally:
            if path:
                try:
                    os.unlink(path)
                except OSError:
                    pass
    return False


_notify_warned = False


def preview(text: str, max_len: int = 80) -> str:
    """One-line preview suitable for a toast body."""
    snippet = text.replace("\r", " ").replace("\n", " ").strip()
    if len(snippet) <= max_len:
        return snippet or "(empty)"
    return snippet[:max_len] + "..."


def notify(title: str, message: str) -> None:
    """Show a desktop toast via OS-native tooling.

    macOS: osascript + AppleScript ``display notification``.
    Windows: PowerShell + System.Windows.Forms.NotifyIcon balloon tip.
    Other platforms / dispatch errors: silent no-op (with a single warning).

    All dispatch is non-blocking (subprocess.Popen, no wait), so callers
    can invoke this from the asyncio loop directly. notify_async further
    isolates the small subprocess.Popen overhead via to_thread.
    """
    global _notify_warned
    try:
        msg = message[:240]
        if sys.platform == "darwin":
            def _osa_escape(s: str) -> str:
                return s.replace("\\", "\\\\").replace('"', '\\"')
            script = (
                f'display notification "{_osa_escape(msg)}" '
                f'with title "{_osa_escape(title)}"'
            )
            subprocess.Popen(
                ["osascript", "-e", script],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
            )
        elif sys.platform == "win32":
            def _ps_escape(s: str) -> str:
                return s.replace("'", "''")
            ps = (
                "[reflection.assembly]::loadwithpartialname("
                "'System.Windows.Forms') | Out-Null;"
                "$n = New-Object System.Windows.Forms.NotifyIcon;"
                "$n.Icon = [System.Drawing.SystemIcons]::Information;"
                f"$n.BalloonTipTitle = '{_ps_escape(title)}';"
                f"$n.BalloonTipText  = '{_ps_escape(msg)}';"
                "$n.Visible = $true;"
                "$n.ShowBalloonTip(3000);"
                "Start-Sleep -Seconds 3;"
                "$n.Dispose()"
            )
            subprocess.Popen(
                ["powershell", "-NoProfile", "-WindowStyle", "Hidden",
                 "-Command", ps],
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=0x08000000,  # CREATE_NO_WINDOW
            )
        # other platforms: silently skip
    except Exception as exc:
        if not _notify_warned:
            log.warning(f"notify failed (further warnings silenced): {exc}")
            _notify_warned = True


async def notify_async(title: str, message: str) -> None:
    """Dispatch notify() on a worker thread so the asyncio loop is never
    blocked by the small subprocess spawn cost."""
    try:
        await asyncio.to_thread(notify, title, message)
    except Exception:
        pass


def parse_peer_arg(value: str) -> tuple[str, int]:
    """Parse a --peer argument: 'host' or 'host:port'."""
    if ":" in value:
        host, _, port_s = value.rpartition(":")
        try:
            port = int(port_s)
        except ValueError:
            raise argparse.ArgumentTypeError(f"invalid port in --peer {value!r}")
    else:
        host = value
        port = DEFAULT_PORT
    host = host.strip()
    if not host:
        raise argparse.ArgumentTypeError(f"empty host in --peer {value!r}")
    return (host, port)


def get_local_ipv4() -> Optional[str]:
    """Best-effort primary IPv4 of this host (the source IP for the default route)."""
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        sock.connect(("8.8.8.8", 80))
        return sock.getsockname()[0]
    except OSError:
        return None
    finally:
        sock.close()


@dataclass
class Config:
    token: str
    port: int
    name: str
    poll_interval: float
    verbose: bool
    peers: list  # list[tuple[str, int]]; manual fallback peers
    no_notify: bool


def parse_args() -> Config:
    parser = argparse.ArgumentParser(
        prog="anyclip",
        description="AnyClip - cross-device clipboard sync over LAN (text only).",
    )
    env_token = os.environ.get("ANYCLIP_TOKEN")
    parser.add_argument(
        "--token",
        default=None,
        help="Shared secret. Both peers must use the same value. "
             "Falls back to ANYCLIP_TOKEN env var, then to "
             "~/.anyclip/config.json (see --save-token).",
    )
    parser.add_argument(
        "--save-token",
        metavar="TOKEN",
        default=None,
        help="Persist the given token to ~/.anyclip/config.json (0600) "
             "and exit. Subsequent runs do not need --token or "
             "ANYCLIP_TOKEN. Use '-' to read the token from stdin.",
    )
    parser.add_argument(
        "--install-autostart",
        action="store_true",
        help="Register AnyClip to launch at user login and exit. "
             "macOS: ~/Library/LaunchAgents/com.anyclip.plist. "
             "Windows: HKCU Run key.",
    )
    parser.add_argument(
        "--uninstall-autostart",
        action="store_true",
        help="Remove the autostart entry and exit.",
    )
    parser.add_argument(
        "--autostart-status",
        action="store_true",
        help="Print whether AnyClip is registered to launch at login, then exit.",
    )
    parser.add_argument("--port", type=int, default=DEFAULT_PORT,
                        help=f"TCP port to listen on (default: {DEFAULT_PORT})")
    parser.add_argument("--name", default=socket.gethostname(),
                        help="Display name for this node (default: hostname)")
    parser.add_argument("--poll", type=float, default=0.5,
                        help="Clipboard poll interval in seconds (default: 0.5)")
    parser.add_argument("--peer", type=parse_peer_arg, action="append", default=[],
                        metavar="HOST[:PORT]",
                        help="Manual peer fallback. Repeatable; coexists with mDNS. "
                             "Useful when mDNS is blocked (e.g. corporate Wi-Fi). "
                             f"Default port: {DEFAULT_PORT}.")
    parser.add_argument("--verbose", "-v", action="store_true",
                        help="Enable DEBUG logging on the console (file log is always DEBUG)")
    parser.add_argument("--no-notify", action="store_true",
                        help="Suppress desktop toast notifications on clipboard sync")
    parser.add_argument(
        "--headless",
        action="store_true",
        help="Skip the menubar/tray GUI and run as a plain daemon. "
             "Default when the GUI dependencies (rumps/pystray) are "
             "unavailable.",
    )
    args = parser.parse_args()

    # Autostart management short-circuits the daemon: invoking any of
    # these does the registry/plist op and exits without starting up.
    if args.autostart_status:
        backend = autostart.get_backend()
        state = "enabled" if backend.is_enabled() else "disabled"
        sys.stdout.write(f"autostart: {state}\n")
        sys.exit(0)
    if args.install_autostart:
        backend = autostart.get_backend()
        backend.enable(
            executable_path=sys.executable,
            args=[os.path.abspath(sys.argv[0]), "--headless"],
        )
        sys.stderr.write("anyclip: autostart enabled\n")
        sys.exit(0)
    if args.uninstall_autostart:
        backend = autostart.get_backend()
        backend.disable()
        sys.stderr.write("anyclip: autostart disabled\n")
        sys.exit(0)

    # --save-token short-circuits everything else.
    if args.save_token is not None:
        token_to_save = args.save_token
        if token_to_save == "-":
            token_to_save = sys.stdin.read().strip()
        if not token_to_save:
            sys.stderr.write("error: --save-token requires a non-empty value\n")
            sys.exit(2)
        config_store.save(config_store.Config(token=token_to_save))
        sys.stderr.write(
            f"anyclip: token saved to {config_store.config_path()} (0600)\n"
        )
        sys.exit(0)

    # Token resolution priority: CLI flag > env var > on-disk config.
    token: Optional[str] = args.token
    token_source = "cli" if token else None
    if not token and env_token:
        token = env_token
        token_source = "env"
    if not token:
        stored = config_store.load()
        if stored is not None:
            token = stored.token
            token_source = "config"

    if not token:
        sys.stderr.write(
            "error: no token configured. Provide one of:\n"
            "  --token <TOKEN>\n"
            "  ANYCLIP_TOKEN environment variable\n"
            "  anyclip --save-token <TOKEN>   (persists to ~/.anyclip/config.json)\n"
        )
        sys.exit(2)

    if token_source == "config":
        # Logging is not configured yet at parse_args() time, so defer
        # the INFO line to run() via the stash on args. We attach it to
        # the returned Config via a small module-level signal instead of
        # widening the dataclass schema.
        _token_loaded_from_config.set(True)

    return Config(
        token=token,
        port=args.port,
        name=args.name,
        poll_interval=max(0.1, args.poll),
        verbose=args.verbose,
        peers=list(args.peer or []),
        no_notify=args.no_notify,
    )


class _BoolSignal:
    """Tiny module-level latch so parse_args() can hand a one-bit signal
    to run() without widening the public Config dataclass schema.
    """

    def __init__(self) -> None:
        self._v = False

    def set(self, value: bool) -> None:
        self._v = value

    def get(self) -> bool:
        return self._v


_token_loaded_from_config = _BoolSignal()


class EchoSuppressor:
    """Tracks the hash of the last item received from a peer per kind.

    The clipboard poller consults this before sending so we don't
    bounce a peer's update right back at them. Text and image are
    tracked separately so an inbound text never accidentally masks
    an outbound image (and vice versa).
    """

    def __init__(self) -> None:
        self._last: dict = {}  # kind -> hash

    def mark_received(self, kind: str, payload_hash: str) -> None:
        self._last[kind] = payload_hash

    def should_send(self, kind: str, payload_hash: str) -> bool:
        return self._last.get(kind) != payload_hash


class ClipboardWatcher:
    READ_FAIL_WARN_AT = 5
    # OS screenshot tools (notably macOS Screenshot.app) drop several
    # representations of the same capture onto the clipboard in quick
    # succession (raw bitmap, TIFF, PNG, ...). Each grab can pick up a
    # different one and re-encode it to PNG with a different size/hash,
    # which used to fire 2-3 sends per logical capture. We ignore image
    # changes that arrive within this many seconds of the previous send.
    IMAGE_COOLDOWN_S = 1.0

    def __init__(self, poll_interval: float, on_change) -> None:
        self.poll_interval = poll_interval
        self.on_change = on_change  # async def (kind: str, data) -> None
        self._consec_read_fails = 0
        self._read_fail_warned = False
        self._last_text: Optional[str] = self._safe_paste()
        # Track raw PNG bytes hash to compare against the next grab without
        # re-hashing megabytes on every poll cycle.
        self._last_image_hash: Optional[str] = None
        # monotonic time of the last image we actually dispatched, used to
        # collapse the multi-representation flood right after a screenshot.
        self._last_image_send_at: float = 0.0
        # Seed image baseline so we do not fire a spurious initial send for
        # whatever happens to be on the clipboard when we start.
        initial_image = grab_clipboard_image()
        if initial_image is not None:
            self._last_image_hash = sha256_bytes(initial_image)
        # File path on the clipboard: cache (path, size, mtime) so we do
        # not re-read megabytes of bytes off disk every poll cycle.
        self._last_file_fp: Optional[tuple] = None
        self._last_file_hash: Optional[str] = None
        self._oversize_file_warned = False
        # Seed file baseline as well.
        for _path in grab_clipboard_files() or []:
            try:
                stat = os.stat(_path)
            except OSError:
                continue
            self._last_file_fp = (_path, stat.st_size, stat.st_mtime_ns)
            break

    def _safe_paste(self) -> Optional[str]:
        try:
            text = pyperclip.paste()
        except Exception as exc:
            self._consec_read_fails += 1
            log.debug(f"clipboard read failed (#{self._consec_read_fails}): {exc}")
            if (self._consec_read_fails >= self.READ_FAIL_WARN_AT
                    and not self._read_fail_warned):
                log.warning(
                    f"clipboard read failing: {self._consec_read_fails} consecutive errors "
                    f"(check OS clipboard permissions / pyperclip backend)"
                )
                self._read_fail_warned = True
            return None
        if self._consec_read_fails:
            self._consec_read_fails = 0
            self._read_fail_warned = False
        return text

    async def run(self) -> None:
        while True:
            # Text path. Empty strings are still treated as a "change"
            # for baseline purposes (so we do not keep re-detecting them)
            # but they are NOT propagated to the peer -- macOS Screenshot
            # briefly clears the clipboard during a capture, and we used
            # to send that as a spurious empty-text frame that surfaced
            # on the remote side as an "(empty)" toast.
            text = self._safe_paste()
            if text is not None and text != self._last_text:
                self._last_text = text
                if text:
                    try:
                        await self.on_change("text", text)
                    except Exception as exc:
                        log.exception(f"on_change(text) handler failed: {exc}")
                else:
                    log.debug("clipboard cleared (empty text); not propagating")

            # Image path. Run in a thread because grabclipboard + PNG
            # encode can take 10s of ms on large bitmaps.
            png = await asyncio.to_thread(grab_clipboard_image)
            if png is not None:
                h = sha256_bytes(png)
                if h != self._last_image_hash:
                    now = time.monotonic()
                    if now - self._last_image_send_at < self.IMAGE_COOLDOWN_S:
                        # Same logical capture re-encoded by another
                        # NSPasteboard representation -- absorb the new
                        # hash silently and drop the send.
                        elapsed = now - self._last_image_send_at
                        log.debug(
                            f"image change within {elapsed:.2f}s of last "
                            f"send (< {self.IMAGE_COOLDOWN_S}s cooldown), dropping"
                        )
                        self._last_image_hash = h
                    else:
                        self._last_image_hash = h
                        self._last_image_send_at = now
                        try:
                            await self.on_change("image", png)
                        except Exception as exc:
                            log.exception(f"on_change(image) handler failed: {exc}")

            # File path. osascript/PowerShell is comparatively expensive,
            # so we only read the bytes off disk when the (path, size,
            # mtime) fingerprint changes, not on every poll.
            await self._check_file_clipboard()

            await asyncio.sleep(self.poll_interval)

    async def _check_file_clipboard(self) -> None:
        files = await asyncio.to_thread(grab_clipboard_files)
        if not files:
            return
        # Only the first file is synced -- multi-file is intentional scope-out.
        path = files[0]
        try:
            stat = await asyncio.to_thread(os.stat, path)
        except OSError:
            return
        fp = (path, stat.st_size, stat.st_mtime_ns)
        if fp == self._last_file_fp:
            return  # nothing new on the clipboard
        # Refuse files that would not fit in a single frame after base64.
        # Reserve ~256 KB for JSON envelope and the b64 1.34x inflation.
        budget = int((MAX_PAYLOAD - 256 * 1024) * 0.74)
        if stat.st_size > budget:
            if not self._oversize_file_warned:
                log.warning(
                    f"file {path!r} too large to sync "
                    f"({stat.st_size} bytes > limit {budget}); skipping"
                )
                self._oversize_file_warned = True
            # Still update the fingerprint so we do not keep re-warning.
            self._last_file_fp = fp
            return
        self._oversize_file_warned = False
        try:
            data = await asyncio.to_thread(Path(path).read_bytes)
        except OSError as exc:
            log.debug(f"file read failed for {path!r}: {exc}")
            return
        self._last_file_fp = fp
        self._last_file_hash = sha256_bytes(data)
        try:
            await self.on_change("file", (os.path.basename(path), data))
        except Exception as exc:
            log.exception(f"on_change(file) handler failed: {exc}")

    def update_local_text(self, text: str) -> None:
        """Set the local text clipboard without re-triggering on_change."""
        self._last_text = text
        try:
            pyperclip.copy(text)
        except Exception as exc:
            log.warning(f"clipboard write (text) failed: {exc}")

    def update_local_image(self, png_bytes: bytes) -> bool:
        """Set the local image clipboard without re-triggering on_change.

        Returns True on success. False means the host platform has no
        write backend (Linux without PIL+xclip etc.) or the OS-native
        helper failed.
        """
        # Update our baseline *before* writing, so even if the OS write
        # races with our poller we will not echo the same image back.
        self._last_image_hash = sha256_bytes(png_bytes)
        ok = set_clipboard_image(png_bytes)
        if not ok:
            log.warning("clipboard write (image) failed or unsupported on this OS")
        return ok

    def update_local_file(self, name: str, data: bytes) -> bool:
        """Save the received file under ~/.anyclip/received/ and put a
        clipboard reference to it on the system clipboard.

        Updates the fingerprint baseline so the very file we just wrote
        is not picked up as a fresh local change on the next poll.
        """
        # Sanitize the name -- accept basename only and strip anything
        # that would land outside the target directory.
        safe = os.path.basename(name).strip() or "received.bin"
        # Drop characters that are awkward on either OS.
        safe = "".join(
            c if c.isalnum() or c in "._- " else "_" for c in safe
        )
        target_dir = LOG_DIR / "received"
        try:
            target_dir.mkdir(parents=True, exist_ok=True)
            target = target_dir / safe
            target.write_bytes(data)
        except OSError as exc:
            log.warning(f"file write to {target_dir} failed: {exc}")
            return False
        self._last_file_hash = sha256_bytes(data)
        # Update fingerprint so the next poll does not echo this back.
        try:
            stat = target.stat()
            self._last_file_fp = (str(target), stat.st_size, stat.st_mtime_ns)
        except OSError:
            self._last_file_fp = None
        ok = set_clipboard_file(str(target))
        if not ok:
            log.warning("clipboard write (file) failed or unsupported on this OS")
        return ok


class AuthGate:
    """Per-IP cooldown after repeated handshake failures.

    After ``MAX_FAILS`` failed handshakes from the same IP, that IP is
    blocked for ``COOLDOWN`` seconds. A successful handshake clears the
    counter. Stale entries (older than COOLDOWN) are swept lazily on
    each check so the dict cannot grow unbounded.
    """

    MAX_FAILS = 5
    COOLDOWN = 60.0

    def __init__(self) -> None:
        self._fails: dict = {}  # ip -> (count, last_ts)
        self._lock = asyncio.Lock()

    async def is_blocked(self, ip: str) -> bool:
        async with self._lock:
            self._sweep_locked()
            entry = self._fails.get(ip)
            if entry is None:
                return False
            count, last = entry
            return count >= self.MAX_FAILS and (time.time() - last) < self.COOLDOWN

    async def record_fail(self, ip: str) -> None:
        async with self._lock:
            count, _ = self._fails.get(ip, (0, 0.0))
            self._fails[ip] = (count + 1, time.time())

    async def record_ok(self, ip: str) -> None:
        async with self._lock:
            self._fails.pop(ip, None)

    def _sweep_locked(self) -> None:
        now = time.time()
        stale = [ip for ip, (_, last) in self._fails.items()
                 if now - last >= self.COOLDOWN]
        for ip in stale:
            self._fails.pop(ip, None)


class PeerLink:
    """Owns the single active TCP link to a peer.

    Acts as both server and client; resolves the simultaneous-connect
    race via lexicographic node_id tie-break.
    """

    def __init__(self, config: Config, node_id: str, on_clip) -> None:
        self.config = config
        self.node_id = node_id
        self.on_clip = on_clip
        self._writer: Optional[asyncio.StreamWriter] = None
        self._peer_node_id: Optional[str] = None
        self._peer_name: Optional[str] = None  # peer's display name (from hello)
        self._linked_at: float = 0.0  # monotonic time when _writer was last set
        self._lock = asyncio.Lock()
        self._token_hash = sha256_hex(config.token)
        self._auth_gate = AuthGate()
        # In-flight outbound attempts keyed by (host, port). Stops two
        # background loops (mDNS reconnect + peer_keepalive) from racing
        # each other into the same address.
        self._connecting: set = set()

    @property
    def active(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    @property
    def peer_name(self) -> Optional[str]:
        return self._peer_name

    async def serve(self) -> None:
        try:
            server = await asyncio.start_server(
                self._handle_inbound, host="0.0.0.0", port=self.config.port,
            )
        except OSError as exc:
            if exc.errno == errno.EADDRINUSE:
                raise FatalStartupError(
                    f"port {self.config.port} still in use after cleanup attempt; "
                    f"another process may have grabbed it -- try again or pick --port"
                ) from exc
            raise
        log.info(f"listening on tcp/{self.config.port}")
        async with server:
            await server.serve_forever()

    async def _handle_inbound(
        self, reader: asyncio.StreamReader, writer: asyncio.StreamWriter,
    ) -> None:
        peer = writer.get_extra_info("peername")
        log.debug(f"inbound from {peer}")
        peer_ip = peer[0] if peer else None
        if peer_ip and await self._auth_gate.is_blocked(peer_ip):
            log.info(
                f"auth gate: {peer_ip} blocked "
                f"(>{AuthGate.MAX_FAILS} failures, cooldown {AuthGate.COOLDOWN:.0f}s)"
            )
            self._safe_close(writer)
            return
        self._enable_keepalive(writer)
        try:
            await self._session(reader, writer, inbound=True)
        except Exception as exc:
            log.debug(f"inbound session ended: {exc}")
        finally:
            self._safe_close(writer)

    async def try_connect(self, host: str, port: int) -> None:
        if self.active:
            return
        key = (host, port)
        if key in self._connecting:
            log.debug(f"connect to {host}:{port} already in flight, skipping")
            return
        self._connecting.add(key)
        try:
            try:
                reader, writer = await asyncio.wait_for(
                    asyncio.open_connection(host, port), timeout=CONNECT_TIMEOUT,
                )
            except Exception as exc:
                log.info(f"connect to {host}:{port} failed: {exc}")
                return
            log.debug(f"outbound connected to {host}:{port}")
            self._enable_keepalive(writer)
            try:
                await self._session(reader, writer, inbound=False)
            except Exception as exc:
                log.debug(f"outbound session ended: {exc}")
            finally:
                self._safe_close(writer)
        finally:
            self._connecting.discard(key)

    @staticmethod
    def _safe_close(writer: asyncio.StreamWriter) -> None:
        try:
            writer.close()
        except Exception:
            pass

    @staticmethod
    def _enable_keepalive(writer: asyncio.StreamWriter) -> None:
        """Turn on TCP keepalive so the OS reaps silent zombies in ~30s.

        Windows ignores the per-socket tuning (its KeepAliveTime is a
        registry value defaulting to ~2 h) -- the tie-breaker stale-link
        replacement handles that gap. On macOS the symbolic constant for
        KEEPIDLE is not in Python stdlib, so we use the raw value 0x10.
        """
        sock = writer.get_extra_info("socket")
        if sock is None:
            return
        try:
            sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
            if sys.platform == "linux":
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPIDLE, 15)
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPINTVL, 5)
                sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPCNT, 3)
            elif sys.platform == "darwin":
                sock.setsockopt(socket.IPPROTO_TCP, 0x10, 15)  # TCP_KEEPALIVE
        except OSError as exc:
            log.debug(f"keepalive setup failed: {exc}")

    async def _session(
        self,
        reader: asyncio.StreamReader,
        writer: asyncio.StreamWriter,
        inbound: bool,
    ) -> None:
        await self._send(writer, {
            "type": "hello",
            "token": self._token_hash,
            "node_id": self.node_id,
            "name": self.config.name,
            # `version` is the legacy single-int field; kept for old peers
            # that only know about it. New peers also read the explicit
            # protocol_major/minor + app_version fields below.
            "version": PROTOCOL_VERSION,
            "app_version": APP_VERSION,
            "protocol_major": PROTOCOL_MAJOR,
            "protocol_minor": PROTOCOL_MINOR,
        })
        peer_ip_for_emit = ""
        try:
            peer = writer.get_extra_info("peername")
            if peer:
                peer_ip_for_emit = peer[0]
        except Exception:
            pass
        try:
            hello = await asyncio.wait_for(self._recv(reader), timeout=HANDSHAKE_TIMEOUT)
        except asyncio.TimeoutError:
            log.warning("handshake timeout")
            emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="timeout"))
            return
        if not hello or hello.get("type") != "hello":
            log.warning("invalid hello, closing")
            emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="invalid"))
            return
        peer_ip = None
        if inbound:
            peer = writer.get_extra_info("peername")
            peer_ip = peer[0] if peer else None
        if hello.get("token") != self._token_hash:
            log.warning(f"auth failed from peer name={hello.get('name')!r}")
            if peer_ip:
                await self._auth_gate.record_fail(peer_ip)
            emit_event(HandshakeFailed(addr=peer_ip or peer_ip_for_emit, reason="auth"))
            return
        # Parse peer's version info with backward-compat defaults: an old
        # peer only sends `version`, so treat that as protocol_major and
        # assume protocol_minor=0 / unknown app_version.
        peer_proto_major_raw = hello.get("protocol_major")
        if not isinstance(peer_proto_major_raw, int):
            peer_proto_major_raw = hello.get("version", 0)
            if not isinstance(peer_proto_major_raw, int):
                peer_proto_major_raw = 0
        peer_proto_minor_raw = hello.get("protocol_minor", 0)
        if not isinstance(peer_proto_minor_raw, int):
            peer_proto_minor_raw = 0
        peer_app_version = hello.get("app_version")
        if not isinstance(peer_app_version, str) or not peer_app_version:
            peer_app_version = "unknown"

        peer_version = VersionInfo(
            app_version=peer_app_version,
            protocol_major=peer_proto_major_raw,
            protocol_minor=peer_proto_minor_raw,
        )
        local_version = VersionInfo(
            app_version=APP_VERSION,
            protocol_major=PROTOCOL_MAJOR,
            protocol_minor=PROTOCOL_MINOR,
        )
        compat = negotiate(local_version, peer_version)
        if not link_allowed(compat):
            log.warning(
                f"version refused: local proto={PROTOCOL_MAJOR}.{PROTOCOL_MINOR} "
                f"app={APP_VERSION} vs peer proto="
                f"{peer_version.protocol_major}.{peer_version.protocol_minor} "
                f"app={peer_version.app_version} -> {compat.value}"
            )
            emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason=f"version:{compat.value}"))
            return
        if compat != Compatibility.COMPATIBLE:
            log.info(
                f"version mismatch (link kept): {compat.value} "
                f"local proto={PROTOCOL_MAJOR}.{PROTOCOL_MINOR} vs peer proto="
                f"{peer_version.protocol_major}.{peer_version.protocol_minor}"
            )
        peer_id = hello.get("node_id")
        if not isinstance(peer_id, str) or peer_id == self.node_id:
            log.debug("self loopback or bad node_id, dropping")
            return
        if peer_ip:
            await self._auth_gate.record_ok(peer_ip)

        async with self._lock:
            if self.active:
                # Two distinct cases:
                #
                # (a) Genuine concurrent connect race. Both peers must agree
                #     on the same surviving socket -- "smaller node_id keeps
                #     outbound, larger node_id keeps inbound" -- so each
                #     peer keeps its own end of the *same* TCP link and
                #     drops the other.
                #
                # (b) Post-race-window arrival. The peer would not be
                #     re-handshaking with us if its end of the existing
                #     link were healthy (mdns_reconnect_loop and
                #     peer_keepalive only fire on link.active==False on
                #     their side). So a fresh, fully-authenticated handshake
                #     after RACE_WINDOW_S is itself proof the existing link
                #     is a zombie (silent TCP death: peer crash, force-kill,
                #     Wi-Fi flap with no FIN/RST). Replace it.
                #
                # Without (b) the larger-id side ends up hugging a dead
                # writer whose `is_closing()` still reports False, and
                # every retry from the peer loops "linked -> peer
                # disconnected" until the peer prunes our address.
                race = (time.monotonic() - self._linked_at) < RACE_WINDOW_S
                if race:
                    keep_this_link = (
                        (not inbound and self.node_id < peer_id) or
                        (inbound and self.node_id > peer_id)
                    )
                    if not keep_this_link:
                        log.debug("tie-breaker: dropping duplicate link (race)")
                        return
                    log.debug("tie-breaker: replacing existing link (race)")
                else:
                    log.info(
                        f"tie-breaker: stale link to {self._peer_name!r} "
                        f"replaced by fresh handshake from {hello.get('name')!r}"
                    )
                self._safe_close(self._writer)  # type: ignore[arg-type]
            self._writer = writer
            self._peer_node_id = peer_id
            self._peer_name = hello.get("name") or peer_id[:8]
            self._linked_at = time.monotonic()

        log.info(
            f"linked with peer name={hello.get('name')!r} "
            f"id={peer_id[:8]} ({'inbound' if inbound else 'outbound'}) "
            f"peer_app_version={peer_version.app_version} "
            f"peer_proto={peer_version.protocol_major}.{peer_version.protocol_minor}"
        )
        emit_event(LinkUp(
            peer_name=str(hello.get("name") or peer_id[:8]),
            peer_id=peer_id,
        ))

        try:
            while True:
                msg = await self._recv(reader)
                if msg is None:
                    break
                msg_type = msg.get("type")
                if msg_type == "clip":
                    kind = msg.get("kind", "text")
                    content = msg.get("content")
                    if kind == "text" and isinstance(content, str):
                        await self.on_clip("text", content)
                    elif kind == "image" and isinstance(content, str):
                        try:
                            png = base64.b64decode(content, validate=True)
                        except Exception as exc:
                            log.warning(f"bad image payload from peer: {exc}")
                            continue
                        await self.on_clip("image", png)
                    elif kind == "file" and isinstance(content, str):
                        try:
                            raw = base64.b64decode(content, validate=True)
                        except Exception as exc:
                            log.warning(f"bad file payload from peer: {exc}")
                            continue
                        name = msg.get("name") or "received.bin"
                        if not isinstance(name, str):
                            name = "received.bin"
                        await self.on_clip("file", (name, raw))
                    else:
                        log.debug(f"ignoring clip with kind={kind!r}")
                elif msg_type == "ping":
                    # Reply with a pong; the actual liveness test is on
                    # the send side -- if drain fails the TCP socket is
                    # dead and _recv returns None next loop.
                    await self._send(writer, {"type": "pong", "ts": time.time()})
                elif msg_type == "pong":
                    # Presence is enough -- the link sent SOMETHING.
                    pass
                else:
                    log.debug(f"ignoring message type: {msg_type!r}")
        finally:
            async with self._lock:
                was_active = self._writer is writer
                if was_active:
                    self._writer = None
                    self._peer_node_id = None
                    self._peer_name = None
            log.info("peer disconnected")
            if was_active:
                emit_event(LinkDown(reason="peer disconnected"))

    async def _send(self, writer: asyncio.StreamWriter, obj: dict) -> None:
        data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
        if len(data) > MAX_PAYLOAD:
            log.warning(f"payload too large ({len(data)} bytes), dropping")
            return
        try:
            writer.write(len(data).to_bytes(4, "big"))
            writer.write(data)
            await writer.drain()
        except Exception as exc:
            log.info(f"send failed (link likely down): {exc}")

    async def _recv(self, reader: asyncio.StreamReader) -> Optional[dict]:
        try:
            head = await reader.readexactly(4)
        except asyncio.IncompleteReadError:
            return None
        n = int.from_bytes(head, "big")
        if n == 0 or n > MAX_PAYLOAD:
            log.warning(f"invalid frame length: {n}")
            return None
        try:
            body = await reader.readexactly(n)
        except asyncio.IncompleteReadError:
            return None
        try:
            return json.loads(body.decode("utf-8"))
        except Exception as exc:
            log.warning(f"bad json: {exc}")
            return None

    async def close(self) -> None:
        """Drop the active link if any. Safe to call multiple times."""
        async with self._lock:
            if self._writer is not None:
                self._safe_close(self._writer)
                self._writer = None
                self._peer_node_id = None
                self._peer_name = None

    async def send_ping(self) -> None:
        """App-layer keepalive frame. Drives traffic on an otherwise idle
        link so a silently-dead TCP socket surfaces as a send failure +
        EOF on the next recv -- important on Windows where the OS
        KeepAliveTime defaults to ~2h.
        """
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        await self._send(writer, {"type": "ping", "ts": time.time()})

    async def send_clip(self, kind: str, content) -> None:
        """Send a clipboard payload. kind=='text' expects str, kind=='image'
        expects raw PNG bytes (will be base64-encoded on the wire)."""
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        if kind == "text":
            if not isinstance(content, str):
                return
            payload = {
                "type": "clip",
                "kind": "text",
                "content": content,
                "hash": sha256_hex(content),
                "ts": time.time(),
            }
        elif kind == "image":
            if not isinstance(content, (bytes, bytearray)):
                return
            encoded = base64.b64encode(bytes(content)).decode("ascii")
            payload = {
                "type": "clip",
                "kind": "image",
                "content": encoded,
                "hash": sha256_bytes(bytes(content)),
                "ts": time.time(),
                "bytes": len(content),
            }
        elif kind == "file":
            # content is expected to be (name, raw_bytes)
            if not isinstance(content, tuple) or len(content) != 2:
                return
            name, raw = content
            if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                return
            raw_b = bytes(raw)
            encoded = base64.b64encode(raw_b).decode("ascii")
            payload = {
                "type": "clip",
                "kind": "file",
                "name": name,
                "content": encoded,
                "hash": sha256_bytes(raw_b),
                "ts": time.time(),
                "bytes": len(raw_b),
            }
        else:
            log.debug(f"send_clip: unknown kind {kind!r}, dropping")
            return
        await self._send(writer, payload)


class MdnsBeacon:
    """Advertises this node and browses for peers on the LAN."""

    def __init__(self, config: Config, node_id: str, on_peer) -> None:
        self.config = config
        self.node_id = node_id
        self.on_peer = on_peer
        self._azc: Optional[AsyncZeroconf] = None
        self._browser: Optional[AsyncServiceBrowser] = None
        self._info: Optional[ServiceInfo] = None
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        # peer_node_id -> (host, port). Used by the mdns reconnect loop to
        # retry peers that we discovered earlier but whose link later dropped
        # without a fresh ServiceStateChange.Added event.
        self.known_peers: dict = {}
        # (host, port) -> consecutive failure count, for pruning dead addrs.
        self.address_fails: dict = {}
        # Best-effort: the IPv4 we baked into the mDNS advertisement at
        # start(). The network watchdog compares this against the current
        # source IP and bounces the daemon if the OS reassigns the IP
        # (zeroconf otherwise keeps shouting into a dead socket -- Errno 49).
        self.advertised_ip: Optional[str] = None
        # Read by permission_probe to decide if Local Network is silently
        # blocked. Bumped on every successful service register and on every
        # mDNS state change we handle (Added/Updated).
        self.events_seen: int = 0

    async def start(self) -> None:
        self._loop = asyncio.get_running_loop()
        self._azc = AsyncZeroconf(ip_version=IPVersion.V4Only)

        instance = f"{self.config.name}-{self.node_id[:8]}.{SERVICE_TYPE}"
        local_ip = get_local_ipv4()
        self.advertised_ip = local_ip
        addresses = [socket.inet_aton(local_ip)] if local_ip else []
        server_host = f"anyclip-{self.node_id[:8]}.local."
        self._info = ServiceInfo(
            type_=SERVICE_TYPE,
            name=instance,
            port=self.config.port,
            addresses=addresses,
            server=server_host,
            properties={
                "id": self.node_id,
                # legacy alias; new peers prefer protocol_major below
                "version": str(PROTOCOL_VERSION),
                "app_version": APP_VERSION,
                "protocol_major": str(PROTOCOL_MAJOR),
                "protocol_minor": str(PROTOCOL_MINOR),
            },
        )
        await self._azc.async_register_service(self._info)
        # Note: we deliberately do NOT bump self.events_seen here.
        # `async_register_service` returns successfully even when the
        # macOS Local Network permission has been revoked -- the OS
        # silently drops the multicast packets. The only signal that
        # actually proves the network half is healthy is an *inbound*
        # mDNS service-state-change in `_handler` below.
        log.info(f"mDNS advertised as {instance!r} ip={local_ip} server={server_host}")

        self._browser = AsyncServiceBrowser(
            self._azc.zeroconf, [SERVICE_TYPE], handlers=[self._handler],
        )

    def _handler(self, zeroconf, service_type, name, state_change) -> None:
        # zeroconf invokes this from its own thread; bounce onto our loop.
        # We handle BOTH Added and Updated. Without Updated we never see
        # a peer come back after the prune logic dropped it: once the
        # zeroconf cache learned about the service the first time, every
        # subsequent re-announcement from the peer arrives as Updated,
        # not Added, so a missed-Added would lock us out indefinitely.
        if state_change not in (
            ServiceStateChange.Added, ServiceStateChange.Updated,
        ):
            return
        loop = self._loop
        if loop is None:
            return
        asyncio.run_coroutine_threadsafe(self._resolve(name), loop)

    async def _resolve(self, name: str) -> None:
        try:
            assert self._azc is not None
            info = await self._azc.async_get_service_info(SERVICE_TYPE, name, timeout=3000)
            if info is None:
                return
            props = {}
            for k, v in (info.properties or {}).items():
                key = k.decode() if isinstance(k, (bytes, bytearray)) else k
                val = v.decode() if isinstance(v, (bytes, bytearray)) else v
                props[key] = val
            peer_id = props.get("id")
            if peer_id == self.node_id:
                # Self-loopback discovery does not prove network is
                # alive (the zeroconf cache resolves locally even when
                # Local Network multicast is blocked). Do not count it.
                return
            # Resolving a *non-self* peer means a multicast Added/Updated
            # actually crossed the network -- the strongest signal we
            # have that Local Network is not silently revoked.
            self.events_seen += 1
            addrs = info.parsed_addresses()
            if not addrs or not info.port:
                return
            host = addrs[0]
            port = info.port
            if isinstance(peer_id, str):
                self.known_peers[peer_id] = (host, port)
            # Fresh advertisement -> the address is alive; clear any
            # pending failure count so the reconnect loop will not prune
            # it on stale data.
            self.address_fails.pop((host, port), None)
            log.info(f"discovered peer {name!r} at {host}:{port}")
            emit_event(PeerDiscovered(name=str(name), addr=f"{host}:{port}"))
            await self.on_peer(host, port)
        except Exception as exc:
            log.warning(f"peer resolve failed for {name!r}: {exc}")

    async def refresh(self) -> None:
        """Re-announce our service and re-issue the browse query.

        Used by ``idle_link_watchdog`` to recover from stale zeroconf
        multicast state (network blip without IP change, sleep/wake,
        Wi-Fi roam) WITHOUT bouncing the whole asyncio runtime. If even
        this fails to bring a peer back, the watchdog escalates to a
        full daemon restart via the supervisor.
        """
        if self._azc is None or self._info is None:
            return
        try:
            await self._azc.async_update_service(self._info)
            log.debug("mDNS: re-announced service")
        except Exception as exc:
            log.warning(f"mDNS re-announce failed: {exc}")
        try:
            if self._browser is not None:
                await self._browser.async_cancel()
        except Exception:
            pass
        try:
            self._browser = AsyncServiceBrowser(
                self._azc.zeroconf, [SERVICE_TYPE], handlers=[self._handler],
            )
            log.debug("mDNS: browser re-issued")
        except Exception as exc:
            log.warning(f"mDNS browser re-issue failed: {exc}")

    async def stop(self) -> None:
        try:
            if self._browser is not None:
                await self._browser.async_cancel()
        except Exception:
            pass
        try:
            if self._info is not None and self._azc is not None:
                await self._azc.async_unregister_service(self._info)
        except Exception:
            pass
        try:
            if self._azc is not None:
                await self._azc.async_close()
        except Exception:
            pass


async def network_watchdog(beacon: "MdnsBeacon", interval: float = 15.0) -> None:
    """Bounce the daemon when the host IPv4 changes.

    zeroconf binds its multicast sender to whatever IP we advertised at
    startup. If macOS/Windows later reassigns the IP (Wi-Fi flap, sleep,
    network switch, VPN toggle) the bound socket becomes invalid and we
    see a flood of ``Errno 49 Can't assign requested address`` -- mDNS
    advertise/resolve quietly stops working without crashing the
    application, so peers keep trying to reach us at the stale IP.

    The cleanest correct response is to restart the whole asyncio runtime
    so beacon.start() picks up the new IP. Raising RuntimeError out of
    asyncio.gather() unwinds run()'s finally (mDNS unregister + PID
    release + listener close) and the supervisor in main() restarts us
    on its existing 1s -> 60s backoff.
    """
    while True:
        await asyncio.sleep(interval)
        previous = beacon.advertised_ip
        if not previous:
            continue
        current = get_local_ipv4()
        if current and current != previous:
            raise RuntimeError(
                f"local IPv4 changed: {previous} -> {current}; "
                f"restarting daemon to re-advertise mDNS"
            )


async def idle_link_watchdog(
    beacon: "MdnsBeacon",
    link: "PeerLink",
    idle_threshold: float = 60.0,
    refresh_attempts_before_bounce: int = 3,
) -> None:
    """Self-heal mDNS when the link sits dead for too long.

    network_watchdog only fires on IP change. If Wi-Fi blips but the
    IP survives, zeroconf's multicast socket can end up silently
    unbound (no Errno, no exception) and stop delivering peer
    advertisements. mdns_reconnect_loop can't help because it depends
    on `known_peers`, which were pruned the last time the link died.

    Recovery escalation:
      1..refresh_attempts: call beacon.refresh() to re-announce + re-issue
         the browse query. Cheap; reuses the existing AsyncZeroconf.
      attempts+1: raise RuntimeError to unwind asyncio.gather() and let
         the supervisor restart the whole runtime with a fresh zeroconf
         socket. Same trick as network_watchdog.

    Counter resets whenever the link comes back up.
    """
    consecutive_idle = 0
    while True:
        await asyncio.sleep(idle_threshold)
        if link.active:
            consecutive_idle = 0
            continue
        consecutive_idle += 1
        elapsed = idle_threshold * consecutive_idle
        if consecutive_idle <= refresh_attempts_before_bounce:
            log.info(
                f"link idle {elapsed:.0f}s; refreshing mDNS "
                f"(attempt {consecutive_idle}/{refresh_attempts_before_bounce})"
            )
            await beacon.refresh()
        else:
            raise RuntimeError(
                f"link idle > {elapsed:.0f}s with no recovery after "
                f"{refresh_attempts_before_bounce} mDNS refresh attempts; "
                f"bouncing daemon to re-bind zeroconf"
            )


async def link_ping_loop(link: "PeerLink", interval: float = 30.0) -> None:
    """Send an app-layer ping on the active link every `interval` seconds.

    Drives traffic so a silently-dead TCP socket surfaces as a send
    failure -- without this, on Windows the OS keepalive defaults to
    ~2h and the link can sit half-dead for hours before either side
    notices.
    """
    while True:
        await asyncio.sleep(interval)
        if link.active:
            await link.send_ping()


async def mdns_reconnect_loop(beacon: "MdnsBeacon", link: "PeerLink") -> None:
    """Retry mDNS-discovered peers when the link drops.

    The zeroconf browser only fires ServiceStateChange.Added on first
    sight. If a TCP link dies (e.g. the OS reassigns our IP and we hit
    EADDRNOTAVAIL on send) but the peer keeps advertising, no new event
    arrives -- so the only chance to reconnect is to remember every peer
    we ever resolved and poll them ourselves.

    Backoff is the same shape as peer_keepalive (1s -> 60s, reset after a
    session that survived 5s). Cheap when the link is up: just a 2s sleep.
    """
    backoff = 1.0
    while True:
        if link.active:
            backoff = 1.0
            await asyncio.sleep(2)
            continue
        # Dedup by (host, port). The same physical peer can leave several
        # stale entries in known_peers because every restart of the remote
        # daemon mints a new node_id, but the address stays the same -- we
        # only need to attempt one outbound per address per cycle.
        peers = list(dict.fromkeys(beacon.known_peers.values()))
        if not peers:
            await asyncio.sleep(2)
            continue
        # Try every known peer in turn; stop early if one of them links up.
        attempted = False
        for host, port in peers:
            if link.active:
                break
            attempted = True
            start = time.monotonic()
            await link.try_connect(host, port)
            elapsed = time.monotonic() - start
            if link.active:
                # Successful link -- clear any failure history for this addr.
                beacon.address_fails.pop((host, port), None)
                if elapsed > 5.0:
                    backoff = 1.0
                break
            # Link is not active *right now*, but if the call took longer
            # than 5 s the handshake clearly succeeded and the session was
            # up for a real time before dropping. That is a healthy peer
            # whose link happened to die after the fact (a tie-breaker
            # winner taking over, a transient network blip, ...) so we
            # explicitly do NOT count it toward the prune threshold.
            if elapsed > 5.0:
                beacon.address_fails.pop((host, port), None)
                continue
            # Real fast-fail (no route, refused, tie-breaker drop in ms).
            fails = beacon.address_fails.get((host, port), 0) + 1
            beacon.address_fails[(host, port)] = fails
            if fails >= MAX_RECONNECT_FAILS:
                stale_ids = [
                    nid for nid, addr in beacon.known_peers.items()
                    if addr == (host, port)
                ]
                for nid in stale_ids:
                    beacon.known_peers.pop(nid, None)
                beacon.address_fails.pop((host, port), None)
                log.info(
                    f"pruned stale peer address {host}:{port} after "
                    f"{fails} failed attempts; awaiting fresh mDNS discovery"
                )
        if link.active:
            continue
        if attempted:
            await asyncio.sleep(min(backoff, 60))
            backoff = min(backoff * 2, 60)
        else:
            await asyncio.sleep(2)


async def peer_keepalive(host: str, port: int, link: "PeerLink") -> None:
    """Maintain an outbound connection to a manually-configured peer.

    Coexists with mDNS-driven discovery: if the link is already active
    (from either source), this just polls until it drops, then retries
    with exponential backoff (1s -> 60s cap). Backoff resets after a
    session that lasted >5s (treated as 'real' uptime).
    """
    backoff = 1.0
    while True:
        if link.active:
            backoff = 1.0
            await asyncio.sleep(2)
            continue
        start = time.monotonic()
        await link.try_connect(host, port)
        elapsed = time.monotonic() - start
        if elapsed > 5.0:
            backoff = 1.0
            continue
        await asyncio.sleep(min(backoff, 60))
        backoff = min(backoff * 2, 60)


async def _run_permission_probe(beacon: "MdnsBeacon") -> None:
    """One-shot macOS Local Network self-diagnosis.

    Runs once 30 seconds after startup. If no mDNS evidence has been
    seen by then we treat it as the user having revoked the Local
    Network permission and emit `PermissionMissing` so the UI shell
    can show its "Local Network blocked" warning. On other platforms
    this coroutine is never scheduled; the call site gates on
    sys.platform.
    """
    result = await permission_probe.probe(
        events_seen_fn=lambda: beacon.events_seen,
        has_network_fn=lambda: get_local_ipv4() is not None,
        wait_seconds=30.0,
    )
    if result == permission_probe.Result.BLOCKED_LOCAL_NETWORK:
        log.warning(
            "permission probe: no mDNS activity in 30s -- "
            "Local Network permission likely blocked"
        )
        emit_event(PermissionMissing(kind="local_network"))
    elif result == permission_probe.Result.NO_NETWORK:
        log.warning("permission probe: no active network interface")
        emit_event(PermissionMissing(kind="no_network"))
    else:
        log.debug("permission probe: ok")


async def run(config: Config) -> None:
    setup_logging(config.verbose)
    if _token_loaded_from_config.get():
        log.info(f"token loaded from config ({config_store.config_path()})")
    init_event_bus()
    prepare_pid_lock(config.port)
    clear_received_dir()
    node_id = str(uuid.uuid4())
    suppressor = EchoSuppressor()

    # Forward declaration for the closure below.
    watcher: ClipboardWatcher

    notify_enabled = not config.no_notify and sys.platform in ("darwin", "win32")

    async def on_remote_clip(kind: str, data) -> None:
        peer = link.peer_name or "peer"
        if kind == "text":
            assert isinstance(data, str)
            suppressor.mark_received("text", sha256_hex(data))
            watcher.update_local_text(data)
            log.info(f"<- received text {len(data)} chars from {peer!r}")
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=preview(data),
                )
        elif kind == "image":
            assert isinstance(data, (bytes, bytearray))
            png = bytes(data)
            suppressor.mark_received("image", sha256_bytes(png))
            ok = await asyncio.to_thread(watcher.update_local_image, png)
            log.info(
                f"<- received image {len(png)} bytes from {peer!r} "
                f"({'written to clipboard' if ok else 'WRITE FAILED'})"
            )
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=f"image ({len(png)//1024} KB)",
                )
        elif kind == "file":
            assert isinstance(data, tuple) and len(data) == 2
            name, raw = data
            raw_b = bytes(raw)
            suppressor.mark_received("file", sha256_bytes(raw_b))
            ok = await asyncio.to_thread(
                watcher.update_local_file, name, raw_b,
            )
            log.info(
                f"<- received file {name!r} {len(raw_b)} bytes from {peer!r} "
                f"({'written to clipboard' if ok else 'WRITE FAILED'})"
            )
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )

    link = PeerLink(config, node_id, on_remote_clip)

    async def on_local_change(kind: str, data) -> None:
        if not link.active:
            return
        if kind == "text":
            assert isinstance(data, str)
            if not suppressor.should_send("text", sha256_hex(data)):
                log.debug("skip echo of just-received text")
                return
            await link.send_clip("text", data)
            peer = link.peer_name or "peer"
            log.info(f"-> sent text {len(data)} chars to {peer!r}")
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip → {peer}",
                    message=preview(data),
                )
        elif kind == "image":
            assert isinstance(data, (bytes, bytearray))
            png = bytes(data)
            if not suppressor.should_send("image", sha256_bytes(png)):
                log.debug("skip echo of just-received image")
                return
            await link.send_clip("image", png)
            peer = link.peer_name or "peer"
            log.info(f"-> sent image {len(png)} bytes to {peer!r}")
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip → {peer}",
                    message=f"image ({len(png)//1024} KB)",
                )
        elif kind == "file":
            assert isinstance(data, tuple) and len(data) == 2
            name, raw = data
            raw_b = bytes(raw)
            if not suppressor.should_send("file", sha256_bytes(raw_b)):
                log.debug("skip echo of just-received file")
                return
            await link.send_clip("file", (name, raw_b))
            peer = link.peer_name or "peer"
            log.info(f"-> sent file {name!r} {len(raw_b)} bytes to {peer!r}")
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip → {peer}",
                    message=f"file: {name} ({len(raw_b)//1024} KB)",
                )

    watcher = ClipboardWatcher(config.poll_interval, on_local_change)
    beacon = MdnsBeacon(config, node_id, link.try_connect)

    log.info(f"AnyClip starting (node {node_id[:8]}, name={config.name!r})")
    if config.peers:
        log.info(f"manual peers: {[f'{h}:{p}' for h, p in config.peers]}")
    try:
        await beacon.start()
        tasks = [
            link.serve(),
            watcher.run(),
            mdns_reconnect_loop(beacon, link),
            network_watchdog(beacon),
            idle_link_watchdog(beacon, link),
            link_ping_loop(link),
        ]
        for host, port in config.peers:
            tasks.append(peer_keepalive(host, port, link))
        if sys.platform == "darwin":
            tasks.append(_run_permission_probe(beacon))
        await asyncio.gather(*tasks)
    finally:
        await link.close()
        await beacon.stop()
        release_pid_lock()
        clear_received_dir()


def main() -> None:
    # Short-circuit CLI flags (token save, autostart install/uninstall/
    # status) need to run argparse regardless of GUI mode, otherwise
    # `AnyClip.exe --save-token X` would silently launch the tray
    # instead of saving and exiting. parse_args() handles these via
    # sys.exit() internally, so the call never returns when one is
    # present.
    _short_circuit_flags = (
        "--save-token", "--install-autostart",
        "--uninstall-autostart", "--autostart-status",
    )
    if any(f in sys.argv for f in _short_circuit_flags):
        parse_args()
        return  # unreachable -- parse_args exits

    # GUI mode routing happens *before* full argparse so the menubar
    # entry point does not require --token on the command line (the
    # onboarding dialog supplies it). The argparse pass still runs
    # later for the headless path so all existing flags keep working.
    headless_flag = "--headless" in sys.argv
    if not headless_flag:
        gui_entry = None
        if sys.platform == "darwin":
            try:
                from app.menubar_mac import launch_gui as gui_entry  # type: ignore
            except ImportError as exc:
                sys.stderr.write(
                    f"anyclip: macOS GUI deps unavailable ({exc}); "
                    "falling back to headless mode\n"
                )
        elif sys.platform == "win32":
            try:
                from app.tray_win import launch_gui as gui_entry  # type: ignore
            except ImportError as exc:
                sys.stderr.write(
                    f"anyclip: Windows GUI deps unavailable ({exc}); "
                    "falling back to headless mode\n"
                )
        if gui_entry is not None:
            gui_entry()
            return
    config = parse_args()
    backoff = 1.0
    while True:
        try:
            asyncio.run(run(config))
            return
        except KeyboardInterrupt:
            sys.stderr.write("\nshutting down\n")
            return
        except SystemExit:
            raise
        except FatalStartupError as exc:
            sys.stderr.write(f"\nanyclip: {exc}\n")
            sys.exit(1)
        except Exception:
            log.exception(f"daemon crashed; restarting in {backoff:.0f}s")
            time.sleep(backoff)
            backoff = min(backoff * 2, 60)


if __name__ == "__main__":
    main()
