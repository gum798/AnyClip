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
import shutil
import signal
import socket
import stat as stat_mod
import subprocess
import sys
import tempfile
import time
import unicodedata
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
# wire-compat key (mismatch = refuse link), PROTOCOL_MINOR is a cumulative
# feature level: minor >= 1 accepts kind:"files", minor >= 2 accepts frames
# up to MAX_PAYLOAD (64 MiB) instead of the legacy 16 MiB, minor >= 3
# rebuilds folder trees from the optional per-entry "path" field. Minor 3 is
# a capability MARKER only -- it gates nothing on the send path.
# CI exports ANYCLIP_BUILD_VERSION from the git tag (without leading "v");
# local source runs default to a dev marker so handshake logs stay readable.
APP_VERSION = os.environ.get("ANYCLIP_BUILD_VERSION", "0.0.0-dev")
PROTOCOL_MAJOR = 1
PROTOCOL_MINOR = 3
# Legacy alias: pre-1.0 peers send a single `version` int. New code treats
# it as equivalent to protocol_major so old<->new handshakes still link.
PROTOCOL_VERSION = PROTOCOL_MAJOR
MAX_PAYLOAD = 64 * 1024 * 1024  # 64 MiB hard cap per frame (fits a ~16 MB pptx)
# The receive cap enforced by peers on protocol minor < 2: they CLOSE the
# session on a bigger frame, so the broadcast fan-out gates per link on this
# value rather than letting an oversize clip tear an old peer's link down.
LEGACY_MAX_PAYLOAD = 16 * 1024 * 1024  # 16 MiB
# Greedy multi-file send budget, applied to the SUM of raw file sizes in one
# "files" clip (reserves ~256 KB for the JSON envelope + base64 1.34x). Same
# value the single-file path used inline. Keep in lockstep with Swift/C#.
FILE_BUDGET = int((MAX_PAYLOAD - 256 * 1024) * 0.74)  # 49,466,572
# Sender-side cap on files per clip; the receiver stays lenient. Raised
# 100 -> 500 in 1.4.0 because document trees pass 100 easily; FILE_BUDGET
# is the real limit and its formula is untouched.
MAX_FILES_PER_CLIP = 500
DEFAULT_PORT = 24816
HANDSHAKE_TIMEOUT = 5.0
CONNECT_TIMEOUT = 5.0
# Base upper bound on a single app-initiated send. A write that parks past
# the budget (full TCP buffer of a half-open/wedged peer, or a lost send
# completion) would otherwise freeze the caller's loop -- the clipboard poll
# loop and the heartbeat self-heal both await sends inline. On timeout we drop
# the link. The effective budget scales with the frame (see send_timeout_for).
SEND_TIMEOUT = 10.0
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
# Full-mesh cap: at most this many simultaneous active links. Shared
# constant across all three implementations; overridable in the Python
# build only via --max-peers (config.json stays token-only).
DEFAULT_MAX_PEERS = 8

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
            if entry.is_dir() and not entry.is_symlink():
                shutil.rmtree(entry)
            else:
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


def aggregate_files_hash(hashes: list) -> str:
    """Echo-suppression key for a multi-file clip. Order-independent: sort
    the per-file sha256 lowercase-hex strings lexicographically, concatenate
    with NO separator, and sha256 the ASCII bytes of that concatenation.
    Identical formula in Swift (WireProtocol) and C# (WireMessage)."""
    joined = "".join(sorted(hashes))
    return hashlib.sha256(joined.encode("ascii")).hexdigest()


def decode_files_payload(msg: dict) -> Optional[list]:
    """Decode a kind:"files" clip into [(name, raw_bytes, relpath|None), ...].

    Strict: any entry with a missing/non-str/non-strict-base64 ``content``,
    a non-object entry, or an empty/missing ``files`` array returns None so
    the caller drops the WHOLE frame (no partial apply). Names come straight
    off the wire (already NFC per the sender); sanitization happens on write.
    The optional ``path`` (protocol 1.3) is verified against every wire rule
    here: a violating path is downgraded to None -- that entry lands flat --
    and NEVER drops the frame. Wire hashes are never trusted -- recomputed
    downstream from decoded bytes."""
    entries = msg.get("files")
    if not isinstance(entries, list) or not entries:
        log.warning("ignoring files clip: empty or non-list 'files' array")
        return None
    decoded: list = []
    for ent in entries:
        if not isinstance(ent, dict):
            log.warning("ignoring files clip: non-object entry")
            return None
        content = ent.get("content")
        if not isinstance(content, str):
            log.warning("ignoring files clip: entry missing string 'content'")
            return None
        try:
            raw = base64.b64decode(content, validate=True)
        except Exception as exc:
            log.warning(f"ignoring files clip: bad base64 entry ({exc})")
            return None
        name = ent.get("name")
        if not isinstance(name, str) or not name:
            name = "received.bin"
        relpath = ent.get("path")
        if relpath is not None and not is_valid_wire_path(relpath, name):
            log.warning(
                f"files clip: rejecting path {relpath!r} for {name!r}; "
                "placing that file flat"
            )
            relpath = None
        decoded.append((name, raw, relpath))
    return decoded


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


def set_clipboard_files(paths: list) -> bool:
    """Place multiple file references on the clipboard in one operation.

    Windows-> PowerShell ``Set-Clipboard -Path`` with N paths.
    macOS  -> not reliably supported for multiple furls (the shipped Mac app
              is the Swift port); the caller places only the first file via
              set_clipboard_file, so this returns False here.
    Other  -> no-op (False).
    """
    if not paths:
        return False
    if sys.platform == "win32":
        try:
            abs_paths = [str(Path(p).resolve()) for p in paths]
            quoted = ",".join("'" + p.replace("'", "''") + "'" for p in abs_paths)
            result = subprocess.run(
                ["powershell", "-NoProfile", "-Command",
                 f"Set-Clipboard -Path {quoted}"],
                capture_output=True, timeout=5,
                creationflags=0x08000000,
            )
            return result.returncode == 0
        except Exception as exc:
            log.warning(f"set_clipboard_files (Windows) failed: {exc}")
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
    max_peers: int = DEFAULT_MAX_PEERS  # mesh cap; --max-peers overrides


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
        "--max-peers", type=int, default=DEFAULT_MAX_PEERS,
        help="Maximum simultaneous mesh links (default: "
             f"{DEFAULT_MAX_PEERS}). New peers beyond the cap are refused; "
             "known peers reconnecting are always admitted.",
    )
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
        exe, extra = autostart.default_launch_command()
        backend.enable(executable_path=exe, args=extra)
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
        max_peers=max(1, args.max_peers),
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


_WIN_RESERVED = {
    "CON", "PRN", "AUX", "NUL",
    *(f"COM{i}" for i in range(1, 10)),
    *(f"LPT{i}" for i in range(1, 10)),
}
_DENYLIST_CHARS = set('\\/<>:"|?*')


def sanitize_filename(name: str) -> str:
    """Cross-platform-safe basename for a received file. Denylist (not the
    old alnum whitelist), so (), &, spaces, and non-ASCII letters survive.
    Keep in lockstep with Swift sanitizeFilename and C# SanitizeFilename.

    1. NFC normalize.
    2. Basename: split on BOTH '/' and '\\', keep the last component.
    3. Replace denylisted chars, control chars (< U+0020), and U+007F with '_'.
    4. Trim TRAILING dots and spaces.
    5. Empty / '.' / '..'  -> 'received.bin'.
    6. Windows reserved device name (stem before the first dot) -> prefix '_'.
    """
    name = unicodedata.normalize("NFC", name)
    name = name.replace("\\", "/").rsplit("/", 1)[-1]
    name = "".join(
        "_" if (ch in _DENYLIST_CHARS or ord(ch) < 0x20 or ord(ch) == 0x7F) else ch
        for ch in name
    )
    name = name.rstrip(". ")
    if name in ("", ".", ".."):
        return "received.bin"
    if name.split(".", 1)[0].upper() in _WIN_RESERVED:
        name = "_" + name
    return name


def uniquify_names(names: list) -> list:
    """Disambiguate duplicate names within one received batch (after
    sanitization). First occurrence keeps its name; later duplicates get
    ' (2)', ' (3)', ... inserted before the LAST extension (a leading dot
    is not an extension: '.env' -> '.env (2)'). A candidate that collides
    with an already-emitted name is bumped further. Keep in lockstep with
    Swift uniquifyNames and C# TextHelpers.UniquifyNames."""
    used = set()
    result = []
    for name in names:
        if name not in used:
            used.add(name)
            result.append(name)
            continue
        dot = name.rfind(".")
        stem, ext = (name, "") if dot <= 0 else (name[:dot], name[dot:])
        n = 2
        candidate = f"{stem} ({n}){ext}"
        while candidate in used:
            n += 1
            candidate = f"{stem} ({n}){ext}"
        used.add(candidate)
        result.append(candidate)
    return result


def uniquify_against(name: str, used) -> str:
    """``uniquify_names`` for ONE more name against a set of names already
    taken. Returns ``name`` when it is free, else the first ' (2)', ' (3)', ...
    variant that is not, with the counter placed exactly where uniquify_names
    puts it, so a collision with the batch and a collision with what is already
    on disk read the same on screen."""
    return uniquify_names(sorted(used) + [name])[-1]


# Wire "path" limits for folder entries (protocol 1.3). Keep in lockstep
# with Swift WireMessage and C# WireMessage.
MAX_PATH_SEGMENTS = 32
MAX_PATH_CHARS = 240


def sanitize_relpath(path: str) -> str:
    """Per-segment sanitize_filename() over a wire "path", rejoined with '/'.
    Used for the length rule on both sides and for the on-disk destination."""
    return "/".join(sanitize_filename(seg) for seg in path.split("/"))


def is_valid_wire_path(path, name: str) -> bool:
    """True when ``path`` obeys EVERY folder-entry rule of protocol 1.3:

    POSIX '/' separators, NFC, relative (no leading '/', no drive letter, no
    '.'/'..' segments, no empty segments, no backslashes), last segment equal
    to the entry's ``name``, at most MAX_PATH_SEGMENTS segments, and a
    sanitized total length of at most MAX_PATH_CHARS characters.

    The sender emits nothing else; the receiver verifies before touching the
    filesystem and falls back to flat placement for a violating entry. Keep
    in lockstep with Swift isValidWirePath and C# IsValidWirePath."""
    if not isinstance(path, str) or not path:
        return False
    if path != unicodedata.normalize("NFC", path):
        return False
    if "\\" in path or path.startswith("/"):
        return False
    if len(path) > 1 and path[1] == ":" and path[0].isalpha():
        return False
    segments = path.split("/")
    if len(segments) > MAX_PATH_SEGMENTS:
        return False
    for seg in segments:
        if not seg or seg in (".", ".."):
            return False
    if segments[-1] != name:
        return False
    return len(sanitize_relpath(path)) <= MAX_PATH_CHARS


def entry_relpath(entry) -> Optional[str]:
    """The folder path of a files-clip entry, or None for a loose file.

    Canonical entry shape is (name, data, relpath|None); the 2-tuple
    (name, data) built by older call sites is read as "loose"."""
    return entry[2] if len(entry) == 3 else None


# Sidecar files no user means to sync. Excluded from a folder walk (log only).
# Keep in lockstep with Swift folderJunkNames and C# FolderJunkNames.
FOLDER_JUNK_NAMES = {".DS_Store", "Thumbs.db", "desktop.ini"}


def expand_folder(path: str) -> list:
    """Recursively list the syncable files under a copied folder.

    Returns [(abs_path, size, mtime_ns, relpath), ...] sorted BYTE-WISE on
    relpath. ``relpath`` is POSIX, NFC, and starts with the folder's own name
    so the receiver rebuilds received/<folder>/... Symlinks are never followed
    (log only, which also makes cycles impossible), junk sidecars are skipped,
    and empty directories simply vanish -- they are not representable on the
    wire. Keep in lockstep with Swift expandFolder and C# ExpandFolder.

    An unreadable subdirectory is reported and skipped, not silently dropped;
    a partial tree is still allowed (same policy as an unreadable FILE). The
    walk also stops as soon as the ABSOLUTE caps are blown -- see below."""
    root = str(path).rstrip("/\\")
    top = unicodedata.normalize("NFC", os.path.basename(root)) or "folder"
    entries: list = []
    total = 0
    truncated = False

    def on_error(exc: OSError) -> None:
        # os.walk swallows scandir failures by default, so an unreadable
        # subtree would vanish and a PARTIAL tree would ship looking complete.
        # We keep the partial tree (same policy as an unreadable FILE) but it
        # is never SILENT.
        log.warning(
            f"folder walk error under {getattr(exc, 'filename', root)!r}: "
            f"{exc}; subtree skipped"
        )

    for dirpath, dirnames, filenames in os.walk(root, onerror=on_error,
                                                followlinks=False):
        keep_dirs = []
        for d in sorted(dirnames):
            if os.path.islink(os.path.join(dirpath, d)):
                log.info(f"folder walk: skipping symlinked dir {d!r} (never followed)")
                continue
            keep_dirs.append(d)
        dirnames[:] = keep_dirs
        for fname in sorted(filenames):
            full = os.path.join(dirpath, fname)
            if fname in FOLDER_JUNK_NAMES:
                log.debug(f"folder walk: skipping junk file {full!r}")
                continue
            if os.path.islink(full):
                log.info(f"folder walk: skipping symlink {full!r} (never followed)")
                continue
            try:
                st = os.stat(full)
            except OSError as exc:
                log.warning(f"folder walk: stat failed for {full!r}: {exc}; skipping")
                continue
            rel = os.path.relpath(full, root).replace(os.sep, "/")
            entries.append((full, st.st_size, st.st_mtime_ns,
                            unicodedata.normalize("NFC", f"{top}/{rel}")))
            total += st.st_size
            # ABSOLUTE-cap early-out. Past MAX_FILES_PER_CLIP files or
            # FILE_BUDGET bytes a folder can never fit ANY remaining budget, so
            # there is nothing to gain from walking the rest -- and this walk
            # runs on EVERY poll, so an unbounded one would re-scan a huge tree
            # forever. The prefix we keep is deliberately one item PAST the cap,
            # which is exactly what folder_fits() needs to reject the folder,
            # and it is a stable prefix so the fingerprint does not churn.
            if len(entries) > MAX_FILES_PER_CLIP or total > FILE_BUDGET:
                truncated = True
                break
        if truncated:
            break
    if truncated:
        log.info(
            f"folder walk: {root!r} is past the absolute cap "
            f"({len(entries)} files / {total} bytes); walk stopped early"
        )
    entries.sort(key=lambda e: e[3].encode("utf-8"))
    return entries


def folder_fits(entries: list, total: int, count: int) -> bool:
    """Per-folder ALL-OR-NOTHING admission against what the clip has left.

    A folder is accepted only if its ENTIRE expansion fits the remaining
    budget and file count -- no partial trees. An empty expansion never
    "fits" (the caller toasts it separately)."""
    if not entries:
        return False
    if count + len(entries) > MAX_FILES_PER_CLIP:
        return False
    return total + sum(size for _p, size, _m, _rel in entries) <= FILE_BUDGET


def scan_selection(paths: list) -> tuple:
    """Stat a clipboard selection ONCE, expanding any folder in it.

    Returns ``(fp, items)``:
      fp    -- ordered [(path, size, mtime_ns), ...] fingerprint. A folder
               contributes its OWN entry plus one per file in its expanded
               tree, so a folder we just sent (or just wrote into received/)
               is not re-detected and a change INSIDE the tree is.
      items -- [(path, os.stat_result, entries | None), ...] in selection
               order; ``entries`` is expand_folder()'s output for a folder and
               None for a plain file, so no caller ever walks twice.
    A path that vanished between grab and stat drops out of both lists."""
    fp: list = []
    items: list = []
    for path in paths:
        try:
            st = os.stat(path)
        except OSError:
            continue
        fp.append((path, st.st_size, st.st_mtime_ns))
        if stat_mod.S_ISDIR(st.st_mode):
            entries = expand_folder(path)
            fp.extend((p, size, mtime) for p, size, mtime, _rel in entries)
            items.append((path, st, entries))
        else:
            items.append((path, st, None))
    return fp, items


def fingerprint_paths(paths: list) -> list:
    """Just the fingerprint half of scan_selection() -- used to baseline the
    watcher against files/folders we placed on the clipboard ourselves."""
    return scan_selection(paths)[0]


def _writable_relpath(ent) -> Optional[str]:
    """The wire path of an entry, re-verified at the write boundary. Decode
    already rejected violators; the writer never trusts its caller either."""
    rel = entry_relpath(ent)
    if rel and is_valid_wire_path(rel, ent[0]):
        return rel
    return None


def plan_received_layout(files: list, existing) -> list:
    """Map a decoded files clip onto destinations under received/.

    Returns [(relative_destination, top_level_item), ...] in batch order, one
    per entry: ``relative_destination`` is '<top>/<sub>/<name>' for a folder
    entry and '<name>' for a loose file (or for any entry whose path violates
    the wire rules -- flat fallback, never a drop). Each segment goes through
    the existing per-name sanitizer (NFC + denylist + reserved names).

    ``existing`` is the set of names already present in received/. A colliding
    top segment becomes '<top>-2', '<top>-3', ... and the SAME replacement is
    applied to every entry sharing that top, so one clip lands in ONE folder.
    Loose entries keep the per-file ' (2)' uniquify, applied against the batch
    AND against ``existing``: received/ holds TREES now, so a loose file named
    like a folder already sitting there would otherwise plan a write straight
    onto a directory."""
    existing = set(existing)
    loose_idx = [i for i, ent in enumerate(files) if _writable_relpath(ent) is None]
    loose = uniquify_names(
        sorted(existing) + [sanitize_filename(files[i][0]) for i in loose_idx]
    )[len(existing):]
    used = existing | set(loose)
    tops: dict = {}
    plan: list = [None] * len(files)
    for pos, i in enumerate(loose_idx):
        plan[i] = (loose[pos], loose[pos])
    for i, ent in enumerate(files):
        rel = _writable_relpath(ent)
        if rel is None:
            continue
        segments = [sanitize_filename(seg) for seg in rel.split("/")]
        raw_top = segments[0]
        if raw_top not in tops:
            candidate, n = raw_top, 2
            while candidate in used:
                candidate = f"{raw_top}-{n}"
                n += 1
            tops[raw_top] = candidate
            used.add(candidate)
        segments[0] = tops[raw_top]
        plan[i] = ("/".join(segments), segments[0])
    return plan


def _write_under(dest, root, data: bytes) -> None:
    """Write ``data`` to ``dest``, refusing to leave ``root``.

    A symlink sitting AT the destination is removed rather than followed:
    received/ is our own scratch directory, and write_bytes() would otherwise
    open through the link and drop peer bytes outside received/. Raises
    ValueError when the resolved destination escapes ``root`` and OSError when
    it cannot be written -- either way the caller falls back to a flat name,
    it never drops the entry."""
    if dest.is_symlink():
        log.warning(f"removing symlink in the way of {dest}")
        dest.unlink()
    dest.resolve().relative_to(root)
    dest.write_bytes(data)


def received_clip_message(files: list) -> str:
    """Toast body for an inbound kind:"files" clip: a clip that is entirely
    ONE folder names that folder, anything else keeps the count wording."""
    tops = set()
    for ent in files:
        rel = entry_relpath(ent)
        if rel is None:
            return f"{len(files)} files"
        tops.add(rel.split("/", 1)[0])
    if len(tops) == 1:
        return f"{tops.pop()} ({len(files)} files)"
    return f"{len(files)} files"


def placed_single_loose_file(files: list, placed: int) -> bool:
    """True when a received files clip ended up as exactly ONE placed
    top-level item and that item is a LOOSE file rather than a folder.

    Lifted out of on_remote_clip (a closure inside run(), untestable in
    isolation) so the decision itself has a unit test. Entries are in batch
    order and plan_received_layout preserves it, so the first placed item
    corresponds to files[0]."""
    return placed == 1 and bool(files) and entry_relpath(files[0]) is None


class ClipboardWatcher:
    READ_FAIL_WARN_AT = 5
    # OS screenshot tools (notably macOS Screenshot.app) drop several
    # representations of the same capture onto the clipboard in quick
    # succession (raw bitmap, TIFF, PNG, ...). Each grab can pick up a
    # different one and re-encode it to PNG with a different size/hash,
    # which used to fire 2-3 sends per logical capture. We ignore image
    # changes that arrive within this many seconds of the previous send.
    IMAGE_COOLDOWN_S = 1.0

    def __init__(self, poll_interval: float, on_change,
                 on_file_skipped=None) -> None:
        self.poll_interval = poll_interval
        self.on_change = on_change  # async def (kind: str, data) -> None
        # Optional async callback fired when a clipboard file is detected
        # but deliberately not synced (folders, unreadable paths). Lets
        # the UI shell surface a toast; None in headless tests.
        self.on_file_skipped = on_file_skipped  # async def (message: str)
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
        # Files on the clipboard: cache an ORDERED list of (path, size,
        # mtime_ns) fingerprints for the whole selection so we neither
        # re-read bytes every poll nor re-detect an unchanged selection.
        self._last_file_fp: Optional[list] = None
        self._last_file_hash: Optional[str] = None
        # Seed the baseline from whatever is already on the clipboard so we do
        # not fire a spurious initial send at startup. A folder contributes its
        # expanded tree, exactly like _check_file_clipboard computes it.
        self._last_file_fp = fingerprint_paths(grab_clipboard_files() or []) or None

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
        paths = await asyncio.to_thread(grab_clipboard_files)
        if not paths:
            return
        # One stat pass over the selection, expanding folders as we go. The
        # walk DOES run on every poll -- noticing an edit deep inside a tree
        # requires it -- but expand_folder() bails out at the absolute caps, so
        # its cost is bounded. What the fingerprint below saves is the re-SEND
        # and the re-READ of every file for an unchanged selection.
        fp, items = await asyncio.to_thread(scan_selection, paths)
        if not fp:
            return
        if fp == self._last_file_fp:
            return  # unchanged selection
        # Record the fingerprint FIRST so a selection we cannot fully sync is
        # never re-detected and retried every poll cycle (folder-skip design).
        self._last_file_fp = fp

        # Selection order, each item consuming the remaining budget/count: a
        # folder is ALL-OR-NOTHING, loose files stay greedy per file.
        accepted = []  # (name, raw_bytes, relpath | None)
        skipped_count = 0
        total = 0
        for path, stat, entries in items:
            if entries is not None:
                folder_name = os.path.basename(path.rstrip("/\\")) or path
                if not entries:
                    await self._notify_file_skipped(
                        "folder is empty; nothing to sync")
                    continue
                if not folder_fits(entries, total, len(accepted)):
                    await self._notify_file_skipped(
                        f"folder too large to sync: {folder_name}")
                    continue
                for full, size, _mtime, rel in entries:
                    try:
                        data = await asyncio.to_thread(Path(full).read_bytes)
                    except OSError as exc:
                        # Admission already passed on the pre-flight sizes; a
                        # single unreadable file is logged and dropped from the
                        # tree rather than failing the whole folder.
                        log.warning(f"file read failed for {full!r}: {exc}; skipping")
                        continue
                    total += size
                    accepted.append((
                        unicodedata.normalize("NFC", os.path.basename(full)),
                        data, rel,
                    ))
                continue
            if len(accepted) >= MAX_FILES_PER_CLIP:
                skipped_count += 1
                continue
            if total + stat.st_size > FILE_BUDGET:
                skipped_count += 1
                continue
            try:
                data = await asyncio.to_thread(Path(path).read_bytes)
            except OSError as exc:
                log.warning(f"file read failed for {path!r}: {exc}; skipping")
                skipped_count += 1
                continue
            total += stat.st_size
            # NFC on the wire (macOS reads filenames in NFD). Keep in lockstep
            # with Swift WireMessage and C# WireMessage.
            name = unicodedata.normalize("NFC", os.path.basename(path))
            accepted.append((name, data, None))

        if skipped_count:
            await self._notify_file_skipped(
                f"{skipped_count} file(s) skipped (too large to sync)"
            )

        if not accepted:
            return
        # A lone LOOSE file keeps the legacy kind:"file" frame; a one-file
        # folder must stay kind:"files" or its path would be dropped.
        if len(accepted) == 1 and accepted[0][2] is None:
            self._last_file_hash = sha256_bytes(accepted[0][1])
            try:
                await self.on_change("file", (accepted[0][0], accepted[0][1]))
            except Exception as exc:
                log.exception(f"on_change(file) handler failed: {exc}")
        else:
            try:
                await self.on_change("files", accepted)
            except Exception as exc:
                log.exception(f"on_change(files) handler failed: {exc}")

    async def _notify_file_skipped(self, message: str) -> None:
        if self.on_file_skipped is None:
            return
        try:
            await self.on_file_skipped(message)
        except Exception as exc:
            log.exception(f"on_file_skipped handler failed: {exc}")

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
        # Cross-platform-safe basename via the shared denylist sanitizer
        # (NFC + traversal strip + reserved-name guard). Keep in lockstep
        # with Swift sanitizeFilename and C# TextHelpers.SanitizeFilename.
        safe = sanitize_filename(name)
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
            self._last_file_fp = [(str(target), stat.st_size, stat.st_mtime_ns)]
        except OSError:
            self._last_file_fp = None
        ok = set_clipboard_file(str(target))
        if not ok:
            log.warning("clipboard write (file) failed or unsupported on this OS")
        return ok

    def update_local_files(self, files: list) -> int:
        """Write a received clip under ~/.anyclip/received/ and place its
        TOP-LEVEL items on the clipboard in one operation. ``files`` is
        [(name, raw_bytes, relpath|None), ...] as decoded from the wire.

        Entries carrying a path rebuild their folder tree (protocol 1.3);
        entries without one keep the flat behavior. Every destination is
        re-checked to stay under received/ after sanitization -- an entry that
        would escape (or whose write fails for any other reason) is written
        flat instead -- a failure costs THAT entry its path, never the rest of
        the clip. macOS places only the FIRST top-level item (AppleScript furl
        limit); Windows places all. Baselines
        the fingerprint (folders expanded) to what was actually PLACED so the
        files we just wrote are not re-detected. Returns the number of items
        placed on the clipboard (0 on failure)."""
        target_dir = LOG_DIR / "received"
        try:
            target_dir.mkdir(parents=True, exist_ok=True)
            root = target_dir.resolve()
            existing = {p.name for p in target_dir.iterdir()}
        except OSError as exc:
            log.warning(f"file write to {target_dir} failed: {exc}")
            return 0
        plan = plan_received_layout(files, existing)
        used = existing | {top for _rel, top in plan}
        written: list = []  # absolute top-level paths, first appearance order
        for ent, (rel, top) in zip(files, plan):
            dest = target_dir / rel
            try:
                dest.parent.mkdir(parents=True, exist_ok=True)
                _write_under(dest, root, bytes(ent[1]))
            except (OSError, ValueError) as exc:
                # A destination that escapes received/, that a directory
                # already occupies, or that simply will not open costs THAT
                # entry its path -- not the rest of the clip. The flat name is
                # uniquified against the batch and against what is on disk, so
                # one fallback can never clobber another entry.
                log.warning(
                    f"received path {rel!r} not writable ({exc}); placing flat")
                top = uniquify_against(sanitize_filename(ent[0]), used)
                used.add(top)
                dest = target_dir / top
                try:
                    _write_under(dest, root, bytes(ent[1]))
                except (OSError, ValueError) as exc2:
                    log.warning(f"file write to {dest} failed: {exc2}; entry skipped")
                    continue
            top_path = str(target_dir / top)
            if top_path not in written:
                written.append(top_path)
        if not written:
            return 0
        if sys.platform == "darwin":
            placed = written[:1]
            ok = set_clipboard_file(placed[0])
        elif sys.platform == "win32":
            placed = list(written)
            ok = set_clipboard_files(placed)
        else:
            placed, ok = [], False
        if not ok:
            log.warning("clipboard write (files) failed or unsupported on this OS")
            placed = []
        self._last_file_fp = fingerprint_paths(placed) or None
        return len(placed)


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


def _safe_close(writer: asyncio.StreamWriter) -> None:
    try:
        writer.close()
    except Exception:
        pass


def _enable_keepalive(writer: asyncio.StreamWriter) -> None:
    """Turn on TCP keepalive so the OS reaps silent zombies in ~30s.

    Windows ignores the per-socket tuning (its KeepAliveTime is a registry
    value defaulting to ~2h) -- the stale-link replacement handles that gap.
    On macOS the KEEPIDLE symbolic constant is absent from Python stdlib, so
    we use the raw value 0x10.
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


def send_timeout_for(nbytes: int, base: float = SEND_TIMEOUT) -> float:
    """Drain budget for a frame of ``nbytes``: the base timeout plus one
    second per MiB (a 1 MiB/s floor). A fixed 10 s could not carry a 64 MiB
    frame over a slow LAN, and a timeout closes the writer.

    Invariant: worst case 64 MiB -> 10 + 64 = 74 s, which stays below the
    90 s per-link staleness deadline (link_ping_loop: 30 s ping x dead
    factor 3), so a legitimately slow big send can never be mistaken for a
    half-open link. Keep in lockstep with Swift/C#.
    """
    return base + nbytes / (1024 * 1024)


def encode_frame(payload: dict) -> bytes:
    """Canonical JSON body bytes for one wire frame (no length prefix).

    The single encoding point for clip frames: the broadcast fan-out encodes
    each distinct payload variant once and reuses the bytes for both the
    per-link size gate and the send.
    """
    return json.dumps(payload, ensure_ascii=False).encode("utf-8")


def link_accepts_frame(link, nbytes: int) -> bool:
    """False when a frame of ``nbytes`` would breach the legacy 16 MiB
    receive cap that this peer still enforces (protocol minor < 2). Such a
    peer closes the session on an over-cap frame, so we skip the send and
    keep the link instead."""
    if nbytes <= LEGACY_MAX_PAYLOAD:
        return True
    return (link.peer_protocol_minor or 0) >= 2


def files_variant_for_link(link) -> str:
    """Which payload variant a multi-file clip takes on this link:
    "files" for a protocol >= 1.1 peer, else the first-file "file" fallback."""
    return "files" if (link.peer_protocol_minor or 0) >= 1 else "file"


def clip_has_folder_entries(data: list) -> bool:
    """True when any entry of a files clip came out of a copied folder."""
    return any(entry_relpath(ent) for ent in data)


def first_loose_entry(data: list) -> Optional[tuple]:
    """The (name, raw) a protocol-1.0 peer gets as the legacy first-file
    fallback. Folder-derived entries are EXCLUDED -- a stray fragment of
    someone's tree is worse than nothing -- so a folder-only clip yields None
    and that link is skipped (log only)."""
    for ent in data:
        if entry_relpath(ent) is None:
            return (ent[0], ent[1])
    return None


def size_skip_message(names: list) -> Optional[str]:
    """One aggregated toast for the peers a clip was too large for, or None
    when nothing was skipped. At most one per clip."""
    if not names:
        return None
    if len(names) == 1:
        return f"clip not sent to {names[0]} (too large for its AnyClip version)"
    return (f"clip not sent to {len(names)} peer(s) "
            "(too large for their AnyClip version)")


async def _write_frame(writer: asyncio.StreamWriter, obj: dict, timeout: float) -> bool:
    """Length-prefixed JSON frame write for the LinkManager handshake.
    Returns True on success; closes the writer and returns False on a wedged
    drain or over-cap payload (mirrors PeerLink._send)."""
    data = encode_frame(obj)
    if len(data) > MAX_PAYLOAD:
        log.warning(f"payload too large ({len(data)} bytes), dropping")
        return False
    try:
        writer.write(len(data).to_bytes(4, "big"))
        writer.write(data)
        await asyncio.wait_for(
            writer.drain(), timeout=send_timeout_for(len(data), timeout))
        return True
    except asyncio.TimeoutError:
        log.info("handshake send timed out; dropping connection")
        _safe_close(writer)
        return False
    except Exception as exc:
        log.info(f"handshake send failed: {exc}")
        return False


async def _read_frame(reader: asyncio.StreamReader) -> Optional[dict]:
    """Length-prefixed JSON frame read for the LinkManager handshake."""
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


class PeerLink:
    """One authenticated peer session (exactly one peer pair).

    Created by LinkManager AFTER the hello exchange and gate; it never reads
    a hello itself -- it receives the parsed identity (peer node_id, name,
    protocol minor, app version) and the already-open reader/writer. Owns the
    receive loop, per-link send, app-layer keepalive, and the per-link
    staleness watchdog. The listening socket, active-link table, gate, and
    broadcast live in LinkManager.
    """

    def __init__(
        self,
        node_id: str,
        peer_node_id: str,
        peer_name: str,
        peer_protocol_minor: int,
        reader: Optional[asyncio.StreamReader],
        writer: Optional[asyncio.StreamWriter],
        on_clip,
        remote_addr: Optional[tuple] = None,
        send_timeout: float = SEND_TIMEOUT,
    ) -> None:
        self.node_id = node_id            # our own node_id
        self.peer_node_id = peer_node_id  # this peer's node_id (table key)
        self.peer_name = peer_name
        self.peer_protocol_minor = peer_protocol_minor
        self._reader = reader
        self._writer = writer
        # async (kind, data, peer_name) -> None; LinkManager's serialized
        # apply-queue enqueue. Never applies to the clipboard directly.
        self._on_clip = on_clip
        # (host, port) for OUTBOUND links (so re-admission can skip an
        # address already backed by a live link); None for inbound.
        self.remote_addr = remote_addr
        self._send_timeout = send_timeout
        self._linked_at = time.monotonic()
        self._last_inbound = time.monotonic()

    @property
    def active(self) -> bool:
        return self._writer is not None and not self._writer.is_closing()

    @property
    def linked_at(self) -> float:
        return self._linked_at

    async def run_recv(self) -> None:
        """Drain frames until EOF/error, enqueuing received clips through the
        on_clip callback (LinkManager's serialized apply queue). Never reads a
        hello -- that already happened in the manager handshake. Closes the
        writer on exit so `active` flips False and the table entry is reaped.
        """
        reader, writer = self._reader, self._writer
        try:
            while True:
                msg = await self._recv(reader)
                if msg is None:
                    break
                self._last_inbound = time.monotonic()
                msg_type = msg.get("type")
                if msg_type == "clip":
                    kind = msg.get("kind", "text")
                    content = msg.get("content")
                    if kind == "text" and isinstance(content, str):
                        await self._on_clip("text", content, self.peer_name)
                    elif kind == "image" and isinstance(content, str):
                        try:
                            png = base64.b64decode(content, validate=True)
                        except Exception as exc:
                            log.warning(f"bad image payload from peer: {exc}")
                            continue
                        await self._on_clip("image", png, self.peer_name)
                    elif kind == "file" and isinstance(content, str):
                        try:
                            raw = base64.b64decode(content, validate=True)
                        except Exception as exc:
                            log.warning(f"bad file payload from peer: {exc}")
                            continue
                        name = msg.get("name") or "received.bin"
                        if not isinstance(name, str):
                            name = "received.bin"
                        await self._on_clip("file", (name, raw), self.peer_name)
                    elif kind == "files":
                        decoded = decode_files_payload(msg)
                        if decoded is None:
                            continue  # whole-frame drop already logged
                        await self._on_clip("files", decoded, self.peer_name)
                    else:
                        log.debug(f"ignoring clip with kind={kind!r}")
                elif msg_type == "ping":
                    await self._send(writer, {"type": "pong", "ts": time.time()})
                elif msg_type == "pong":
                    pass
                else:
                    log.debug(f"ignoring message type: {msg_type!r}")
        finally:
            _safe_close(writer)
            self._writer = None

    async def _send(self, writer: asyncio.StreamWriter, obj: dict) -> None:
        await self._send_bytes(writer, encode_frame(obj))

    async def _send_bytes(self, writer: asyncio.StreamWriter, data: bytes) -> None:
        if len(data) > MAX_PAYLOAD:
            log.warning(f"payload too large ({len(data)} bytes), dropping")
            return
        try:
            writer.write(len(data).to_bytes(4, "big"))
            writer.write(data)
            await asyncio.wait_for(
                writer.drain(),
                timeout=send_timeout_for(len(data), self._send_timeout),
            )
        except asyncio.TimeoutError:
            # The write parked past the budget -- half-open/wedged socket.
            # Close the writer so the next _recv returns EOF and the session
            # tears down; never let a stuck send freeze the caller's loop.
            log.info("send timed out (link wedged); dropping link to force reconnect")
            _safe_close(writer)
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
        """Drop this link's socket. Safe to call multiple times."""
        if self._writer is not None:
            _safe_close(self._writer)
            self._writer = None

    async def send_ping(self) -> None:
        """App-layer keepalive frame. Drives traffic on an otherwise idle
        link so a silently-dead TCP socket surfaces as a send failure + EOF
        on the next recv -- important on Windows where the OS KeepAliveTime
        defaults to ~2h."""
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        await self._send(writer, {"type": "ping", "ts": time.time()})

    def seconds_since_inbound(self) -> Optional[float]:
        """Seconds since the last inbound frame, or None if not linked. The
        per-link heartbeat compares this against its deadline."""
        if not self.active:
            return None
        return time.monotonic() - self._last_inbound

    def drop_stale_link(self, idle_seconds: float) -> None:
        """Drop a link gone silent -- half-open socket: the peer slept or
        vanished without RST/FIN, so sends never error and _recv never
        returns. Closing the writer makes the next _recv return None, the
        session tears down (freeing the table slot), and re-admission runs.
        No-op if already unlinked."""
        writer = self._writer
        if writer is None:
            return
        log.info(
            f"link to {self.peer_name!r} idle {int(idle_seconds)}s with no "
            f"inbound (peer likely asleep / half-open); dropping to force reconnect"
        )
        _safe_close(writer)

    async def send_frame(self, data: bytes) -> None:
        """Send an already-encoded frame body on THIS link. Used by the mesh
        broadcast so one payload variant is encoded once and reused for the
        per-link size gate and every send of that variant."""
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        await self._send_bytes(writer, data)

    async def send_clip(self, kind: str, content) -> None:
        """Send one clipboard payload to THIS peer. kind=='text' expects str,
        'image' raw PNG bytes, 'file' (name, raw), 'files' [(name, raw), ...]."""
        writer = self._writer
        if writer is None or writer.is_closing():
            return
        payload = build_clip_payload(kind, content)
        if payload is None:
            return
        await self._send(writer, payload)


def build_clip_payload(kind: str, content) -> Optional[dict]:
    """Build the wire payload dict for one clipboard item, or None when the
    content does not match the kind. Field order is part of the wire contract
    (golden vectors); keep in lockstep with Swift/C#."""
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
    elif kind == "files":
        if not isinstance(content, list) or not content:
            return
        files_arr = []
        hashes = []
        total = 0
        for ent in content:
            if not isinstance(ent, tuple) or len(ent) not in (2, 3):
                return
            name, raw = ent[0], ent[1]
            relpath = entry_relpath(ent)
            if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                return
            raw_b = bytes(raw)
            h = sha256_bytes(raw_b)
            entry = {
                "name": name,
                "content": base64.b64encode(raw_b).decode("ascii"),
                "hash": h,
                "bytes": len(raw_b),
            }
            # Optional folder path (protocol 1.3), appended LAST and emitted
            # only when it obeys every wire rule -- so a loose file's entry
            # stays byte-identical to what 1.3.0 sent and a malformed local
            # path degrades to flat placement instead of poisoning the frame.
            if relpath is not None:
                if is_valid_wire_path(relpath, name):
                    entry["path"] = relpath
                else:
                    log.warning(
                        f"dropping invalid folder path {relpath!r} for {name!r}")
            files_arr.append(entry)
            hashes.append(h)
            total += len(raw_b)
        payload = {
            "type": "clip",
            "kind": "files",
            "files": files_arr,
            "hash": aggregate_files_hash(hashes),
            "ts": time.time(),
            "bytes": total,
        }
    else:
        log.debug(f"build_clip_payload: unknown kind {kind!r}, dropping")
        return None
    return payload


class LinkManager:
    """Owns the listening socket, the active-link table keyed by peer
    node_id, the pre-routing gate, and clip broadcast. Splits the old
    single-link PeerLink into a router (this) + N per-peer PeerLink sessions.
    """

    def __init__(self, config: "Config", node_id: str, on_clip,
                 max_peers: int = DEFAULT_MAX_PEERS) -> None:
        self.config = config
        self.node_id = node_id
        # async (kind, data, peer_name) -> None; the REAL apply handler
        # (on_remote_clip), invoked serially by apply_loop.
        self._on_clip = on_clip
        self.max_peers = max_peers
        self._token_hash = sha256_hex(config.token)
        self._auth_gate = AuthGate()          # moved here: fails aggregate per IP
        self._links: dict = {}                # peer node_id -> PeerLink
        self._lock = asyncio.Lock()
        self._apply_queue: asyncio.Queue = asyncio.Queue()
        self._connecting: set = set()         # (host, port) dials in flight
        self._beacon = None                   # set via attach_beacon

    # ---- introspection -------------------------------------------------
    def active_count(self) -> int:
        return sum(1 for l in self._links.values() if l.active)

    def has_link_to_addr(self, host: str, port: int) -> bool:
        key = (host, port)
        return any(l.active and l.remote_addr == key for l in self._links.values())

    def peer_names(self) -> list:
        return [l.peer_name for l in self._links.values() if l.active]

    def attach_beacon(self, beacon) -> None:
        """Wire the discovery snapshot so link drops can actively re-dial."""
        self._beacon = beacon

    # ---- serialized receive apply --------------------------------------
    async def _enqueue_received(self, kind, data, peer_name) -> None:
        await self._apply_queue.put((kind, data, peer_name))

    async def apply_loop(self) -> None:
        """Single consumer across ALL links: applies received clips one at a
        time so the global EchoSuppressor slot stays consistent (each apply
        marks the suppressor before touching the clipboard, in on_clip)."""
        while True:
            kind, data, peer_name = await self._apply_queue.get()
            try:
                await self._on_clip(kind, data, peer_name)
            except Exception as exc:
                log.exception(f"apply handler failed: {exc}")

    # ---- inbound listener ----------------------------------------------
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
        # start_server() already began accepting; park until cancelled.
        # We deliberately avoid Server.serve_forever() / `async with server`:
        # on Python 3.12.1+ both await Server.wait_closed() on their
        # cancellation path, which blocks until every accepted connection
        # drops. During shutdown those are still held open by live PeerLink
        # recv loops, so that await would deadlock teardown. close() alone
        # (no wait_closed) tears the listener down promptly; live links are
        # dropped separately via close().
        try:
            await asyncio.Event().wait()
        finally:
            server.close()
            close_clients = getattr(server, "close_clients", None)
            if close_clients is not None:
                close_clients()  # 3.13+: drop accepted conns promptly

    async def _handle_inbound(self, reader, writer) -> None:
        peer = writer.get_extra_info("peername")
        log.debug(f"inbound from {peer}")
        peer_ip = peer[0] if peer else None
        # AuthGate IP-block check up front so failures aggregate per source IP
        # across connections that never form a link.
        if peer_ip and await self._auth_gate.is_blocked(peer_ip):
            log.info(
                f"auth gate: {peer_ip} blocked "
                f"(>{AuthGate.MAX_FAILS} failures, cooldown {AuthGate.COOLDOWN:.0f}s)"
            )
            _safe_close(writer)
            return
        _enable_keepalive(writer)
        try:
            await self._handshake_and_route(reader, writer, inbound=True)
        except Exception as exc:
            log.debug(f"inbound handshake failed: {exc}")
            _safe_close(writer)

    # ---- outbound dialer -----------------------------------------------
    async def ensure_link(self, host: str, port: int) -> None:
        """Dial a discovered peer if we have capacity and no live link to it.
        Returns promptly: on success the session serves in a background task."""
        if self.active_count() >= self.max_peers:
            return
        key = (host, port)
        if key in self._connecting:
            log.debug(f"connect to {host}:{port} already in flight, skipping")
            return
        if self.has_link_to_addr(host, port):
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
            _enable_keepalive(writer)
            try:
                await self._handshake_and_route(
                    reader, writer, inbound=False, remote_addr=key,
                )
            except Exception as exc:
                log.debug(f"outbound handshake failed: {exc}")
                _safe_close(writer)
        finally:
            self._connecting.discard(key)

    # ---- handshake + pre-routing gate ----------------------------------
    async def _handshake_and_route(self, reader, writer, inbound,
                                   remote_addr=None) -> None:
        ok = await _write_frame(writer, {
            "type": "hello",
            "token": self._token_hash,
            "node_id": self.node_id,
            "name": self.config.name,
            "version": PROTOCOL_VERSION,
            "app_version": APP_VERSION,
            "protocol_major": PROTOCOL_MAJOR,
            "protocol_minor": PROTOCOL_MINOR,
        }, timeout=SEND_TIMEOUT)
        if not ok:
            _safe_close(writer)
            return
        peer_ip_for_emit = ""
        try:
            peer = writer.get_extra_info("peername")
            if peer:
                peer_ip_for_emit = peer[0]
        except Exception:
            pass
        try:
            hello = await asyncio.wait_for(_read_frame(reader), timeout=HANDSHAKE_TIMEOUT)
        except asyncio.TimeoutError:
            log.warning("handshake timeout")
            emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="timeout"))
            _safe_close(writer)
            return
        if not hello or hello.get("type") != "hello":
            log.warning("invalid hello, closing")
            emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="invalid"))
            _safe_close(writer)
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
            _safe_close(writer)
            return
        # Version parse with backward-compat defaults (old peer: only `version`).
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
            _safe_close(writer)
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
            _safe_close(writer)
            return
        if peer_ip:
            await self._auth_gate.record_ok(peer_ip)
        peer_name = str(hello.get("name") or peer_id[:8])
        link = await self._route(
            peer_id, peer_name, peer_proto_minor_raw, peer_version.app_version,
            reader, writer, inbound, remote_addr,
        )
        if link is None:
            _safe_close(writer)
            return
        # Serve in the background so the dialer/accept loop moves on to other
        # peers. run_recv owns the writer for the rest of the link's life.
        asyncio.create_task(self._serve_link(link, peer_id))

    # ---- routing: replacement / tie-break / cap ------------------------
    def _keep_new(self, inbound: bool, peer_id: str) -> bool:
        """Existing tie-break rule for the genuine simultaneous-connect race:
        smaller node_id keeps its OUTBOUND end, larger keeps its INBOUND end."""
        return ((not inbound and self.node_id < peer_id) or
                (inbound and self.node_id > peer_id))

    async def _route(self, peer_id, peer_name, minor, app_version,
                     reader, writer, inbound, remote_addr):
        """Under the lock, decide accept/replace/refuse for an authenticated
        connection and, on accept, install a PeerLink in the table. Returns
        the installed PeerLink, or None if refused/dropped (caller closes)."""
        async with self._lock:
            existing = self._links.get(peer_id)
            if existing is not None:
                # Known node_id: reconnect/duplicate/race. Never refused by
                # the cap. The tie-break applies only inside the race window;
                # otherwise a fresh authenticated handshake for a live link
                # means the peer considers the old link dead -> replace it.
                if existing.active:
                    race = (time.monotonic() - existing.linked_at) < RACE_WINDOW_S
                    if race and not self._keep_new(inbound, peer_id):
                        log.debug("tie-breaker: dropping duplicate link (race)")
                        return None
                    if race:
                        log.debug("tie-breaker: replacing existing link (race)")
                    else:
                        log.info(
                            f"replacing link with {existing.peer_name!r} "
                            f"(peer reconnected)"
                        )
                await existing.close()
                self._links.pop(peer_id, None)
            else:
                # New node_id -> enforce the peer cap.
                if len(self._links) >= self.max_peers:
                    log.info(
                        f"peer cap reached ({len(self._links)}); refusing {peer_name!r}"
                    )
                    return None
            link = PeerLink(
                self.node_id, peer_id, peer_name, minor, reader, writer,
                self._enqueue_received, remote_addr=remote_addr,
            )
            self._links[peer_id] = link
        log.info(
            f"linked with peer name={peer_name!r} id={peer_id[:8]} "
            f"({'inbound' if inbound else 'outbound'}) app_version={app_version} "
            f"peer_proto={PROTOCOL_MAJOR}.{minor}"
        )
        emit_event(LinkUp(
            node_id=peer_id, peer_name=peer_name,
            app_version=app_version, protocol_minor=minor,
        ))
        return link

    async def _serve_link(self, link, peer_id) -> None:
        """Run one link's receive loop + its own per-link staleness watchdog.
        On teardown, remove the table entry (freeing a cap slot), emit
        LinkDown, and re-scan discovery to re-admit a waiting peer."""
        ping_task = asyncio.create_task(link_ping_loop(link))
        try:
            await link.run_recv()
        finally:
            ping_task.cancel()
            try:
                await ping_task
            except asyncio.CancelledError:
                pass
            removed = False
            async with self._lock:
                # Identity guard: a replacement already swapped the table
                # entry, so the replaced link must NOT emit LinkDown.
                if self._links.get(peer_id) is link:
                    del self._links[peer_id]
                    removed = True
            if removed:
                log.info(f"peer {link.peer_name!r} disconnected")
                emit_event(LinkDown(node_id=peer_id, reason="peer disconnected"))
                if self._beacon is not None:
                    await self.redial_discovered(self._beacon)

    async def _drop_link(self, link) -> None:
        """Force-close one link (used on a broadcast send failure)."""
        await link.close()

    # ---- broadcast -----------------------------------------------------
    def _gate(self, link, frame: bytes, skipped: list) -> bool:
        """Per-link legacy size gate. Records the peer name and returns False
        when this frame would breach the 16 MiB cap a pre-1.2 peer enforces --
        that peer would close the session, so we skip the send and KEEP the
        link. The caller emits one aggregated toast for `skipped`."""
        if link_accepts_frame(link, len(frame)):
            return True
        log.info(
            f"clip too large for {link.peer_name!r} "
            "(peer protocol < 1.2); skipping"
        )
        skipped.append(link.peer_name)
        return False

    async def broadcast_clip(self, kind, content) -> tuple:
        """Fan out a simple (text/image/file) clip to all active links; a
        per-link failure drops only that link. The frame is encoded ONCE and
        the same bytes are reused for the size gate and every send. Returns
        (sent, skipped_names) so the caller can log/toast once."""
        skipped: list = []
        payload = build_clip_payload(kind, content)
        if payload is None:
            return 0, skipped
        frame = encode_frame(payload)
        sent = 0
        for link in list(self._links.values()):
            if not link.active:
                continue
            if not self._gate(link, frame, skipped):
                continue
            try:
                await link.send_frame(frame)
            except Exception as exc:
                log.info(f"send to {link.peer_name!r} failed: {exc}; dropping link")
                await self._drop_link(link)
                continue
            sent += 1
        return sent, skipped

    async def broadcast_files(self, data) -> tuple:
        """Fan out a multi-file selection with per-link minor gating. Returns
        (sent_full, sent_fallback, max_dropped, skipped_names) aggregated
        across links for a single toast each. The global echo check is done by
        the caller. Each distinct payload variant ("files" for a protocol >=
        1.1 peer, the first-file "file" fallback otherwise) is encoded at most
        once per broadcast and reused for both the size gate and the send.

        Folder-derived entries are excluded from the minor-0 fallback, so a
        folder-only clip sends NOTHING on such a link; a minor 1-2 peer gets
        the same frame and flattens it (logged once per clip per link)."""
        sent_full = sent_fallback = max_dropped = 0
        skipped: list = []
        frames: dict = {}
        folder_clip = clip_has_folder_entries(data)

        def frame_for(variant: str) -> Optional[bytes]:
            if variant not in frames:
                if variant == "files":
                    payload = build_clip_payload("files", data)
                else:
                    loose = first_loose_entry(data)
                    payload = None if loose is None else build_clip_payload(
                        "file", (loose[0], bytes(loose[1])))
                frames[variant] = None if payload is None else encode_frame(payload)
            return frames[variant]

        for link in list(self._links.values()):
            if not link.active:
                continue
            variant = files_variant_for_link(link)
            frame = frame_for(variant)
            if frame is None:
                if variant == "file" and folder_clip:
                    log.info(
                        f"folder-only clip not sent to {link.peer_name!r} "
                        "(peer protocol 1.0)"
                    )
                continue
            if (folder_clip and variant == "files"
                    and (link.peer_protocol_minor or 0) < 3):
                log.info(
                    f"peer {link.peer_name} will flatten folders (protocol < 1.3)")
            if not self._gate(link, frame, skipped):
                continue
            try:
                await link.send_frame(frame)
            except Exception as exc:
                log.info(f"send to {link.peer_name!r} failed: {exc}; dropping link")
                await self._drop_link(link)
                continue
            if variant == "files":
                sent_full += 1
            else:  # legacy first-file fallback for a minor-0 peer
                sent_fallback += 1
                max_dropped = max(max_dropped, len(data) - 1)
        return sent_full, sent_fallback, max_dropped, skipped

    # ---- re-admission --------------------------------------------------
    async def redial_discovered(self, beacon) -> None:
        """Dial every discovered peer we are not yet linked to, up to the cap.
        Called on link drop and each retry cycle. Keyed by the advertised
        node_id (== the peer's hello node_id); addresses already backing a
        live link are skipped."""
        linked_addrs = {l.remote_addr for l in self._links.values()
                        if l.active and l.remote_addr}
        for nid, (host, port) in list(beacon.known_peers.items()):
            if nid == self.node_id:
                continue
            if self.active_count() >= self.max_peers:
                break
            existing = self._links.get(nid)
            if existing is not None and existing.active:
                continue
            if (host, port) in linked_addrs:
                continue
            await self.ensure_link(host, port)

    async def close(self) -> None:
        """Drop every active link. Safe to call multiple times."""
        for link in list(self._links.values()):
            await link.close()
        self._links.clear()


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
    manager: "LinkManager",
    idle_threshold: float = 60.0,
    refresh_attempts_before_bounce: int = 3,
) -> None:
    """Self-heal mDNS when the WHOLE mesh sits dead for too long.

    network_watchdog only fires on IP change. If Wi-Fi blips but the IP
    survives, zeroconf's multicast socket can end up silently unbound (no
    Errno, no exception) and stop delivering peer advertisements.
    mdns_reconnect_loop can't help because it depends on `known_peers`,
    which were pruned the last time the links died.

    Global scope, keyed on the manager's ACTIVE-LINK COUNT (never on a
    single link's idleness): keyed per-link, one sleeping peer would bounce
    the daemon and tear down every healthy link. Only when ZERO links are
    active do we escalate.

    Recovery escalation:
      1..refresh_attempts: call beacon.refresh() to re-announce + re-issue
         the browse query. Cheap; reuses the existing AsyncZeroconf.
      attempts+1: raise RuntimeError to unwind asyncio.gather() and let the
         supervisor restart the whole runtime with a fresh zeroconf socket.

    Counter resets whenever any link is active.
    """
    consecutive_idle = 0
    while True:
        await asyncio.sleep(idle_threshold)
        if manager.active_count() > 0:
            consecutive_idle = 0
            continue
        consecutive_idle += 1
        elapsed = idle_threshold * consecutive_idle
        if consecutive_idle <= refresh_attempts_before_bounce:
            log.info(
                f"no active links for {elapsed:.0f}s; refreshing mDNS "
                f"(attempt {consecutive_idle}/{refresh_attempts_before_bounce})"
            )
            await beacon.refresh()
        else:
            raise RuntimeError(
                f"no active links > {elapsed:.0f}s with no recovery after "
                f"{refresh_attempts_before_bounce} mDNS refresh attempts; "
                f"bouncing daemon to re-bind zeroconf"
            )


async def link_ping_loop(
    link: "PeerLink", interval: float = 30.0, dead_factor: float = 3.0
) -> None:
    """App-layer heartbeat on the active link. Two jobs:

    1. Ping every `interval`s, so an actively broken socket surfaces as a
       send failure + EOF (on Windows the OS keepalive defaults to ~2h).
    2. Enforce a liveness deadline. A half-open socket -- the peer slept or
       vanished without RST/FIN -- accepts our pings silently and never
       delivers EOF, so _recv parks forever and the link is a permanent
       zombie. Detection can't rely on send failures; we require *inbound*
       traffic (the peer pongs our pings). If nothing arrives for
       `interval * dead_factor`, the link is dead -- drop it so the reconnect
       loop runs. (Field bug: a Mac held a dead link for ~50 min after its
       peer slept, which in turn made the peer idle-bounce forever.)
    """
    while True:
        await asyncio.sleep(interval)
        if not link.active:
            continue
        await link.send_ping()
        idle = link.seconds_since_inbound()
        if idle is not None and idle > interval * dead_factor:
            link.drop_stale_link(idle)


async def mdns_reconnect_loop(beacon: "MdnsBeacon", manager: "LinkManager") -> None:
    """Retry mDNS-discovered peers to keep the mesh filled up to the cap.

    The zeroconf browser only fires ServiceStateChange.Added on first sight.
    If a TCP link dies (e.g. the OS reassigns our IP and we hit
    EADDRNOTAVAIL on send) but the peer keeps advertising, no new event
    arrives -- so the only chance to reconnect is to remember every peer we
    ever resolved and poll them ourselves. In the mesh this feeds N links:
    dial every known address not already backing a live link, up to the cap.

    Backoff is the same shape as peer_keepalive (1s -> 60s, reset after a
    session that survived 5s). Cheap when the mesh is full: just a 2s sleep.
    """
    backoff = 1.0
    while True:
        if manager.active_count() >= manager.max_peers:
            backoff = 1.0
            await asyncio.sleep(2)
            continue
        # Dedup by (host, port). The same physical peer can leave several
        # stale entries in known_peers (every remote restart mints a new
        # node_id at the same address) -- one outbound per address per cycle.
        peers = list(dict.fromkeys(beacon.known_peers.values()))
        if not peers:
            await asyncio.sleep(2)
            continue
        attempted = False
        for host, port in peers:
            if manager.active_count() >= manager.max_peers:
                break
            if manager.has_link_to_addr(host, port):
                continue
            attempted = True
            start = time.monotonic()
            await manager.ensure_link(host, port)
            elapsed = time.monotonic() - start
            if manager.has_link_to_addr(host, port):
                # Linked -- clear any failure history for this addr.
                beacon.address_fails.pop((host, port), None)
                if elapsed > 5.0:
                    backoff = 1.0
                continue
            # Not linked right now, but a >5s call means the handshake
            # succeeded and the session ran before dropping -- a healthy peer
            # whose link died after the fact. Do NOT count it toward pruning.
            if elapsed > 5.0:
                beacon.address_fails.pop((host, port), None)
                continue
            # Real fast-fail (no route, refused, over-cap short-circuit, ...).
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
        if attempted:
            await asyncio.sleep(min(backoff, 60))
            backoff = min(backoff * 2, 60)
        else:
            await asyncio.sleep(2)


async def peer_keepalive(host: str, port: int, manager: "LinkManager") -> None:
    """Maintain an outbound connection to a manually-configured peer.

    Coexists with mDNS-driven discovery: if a live link to this address
    already exists (from either source), this just polls until it drops,
    then retries with exponential backoff (1s -> 60s cap). Backoff resets
    after a session that lasted >5s (treated as 'real' uptime).
    """
    backoff = 1.0
    while True:
        if manager.has_link_to_addr(host, port):
            backoff = 1.0
            await asyncio.sleep(2)
            continue
        start = time.monotonic()
        await manager.ensure_link(host, port)
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


async def send_files_to_link(link, data) -> tuple:
    """Per-link minor gating for a multi-file clip (NO echo check), on ONE
    link:
      minor >= 1 -> one kind:"files" clip, returns ("files", len(data)).
      minor 0    -> first LOOSE file as legacy kind:"file", returns
                    ("file", dropped); a folder-only clip has no loose file,
                    so nothing is sent and it returns ("skipped", 0).

    The mesh fan-out does not call this -- LinkManager.broadcast_files picks
    the same variant via files_variant_for_link() but encodes each variant
    once and applies the legacy size gate before sending. Keep the variant
    choice here in lockstep with that.
    """
    if files_variant_for_link(link) == "files":
        await link.send_clip("files", data)
        return ("files", len(data))
    loose = first_loose_entry(data)
    if loose is None:
        log.info(
            f"folder-only clip not sent to {link.peer_name!r} (peer protocol 1.0)")
        return ("skipped", 0)
    await link.send_clip("file", (loose[0], bytes(loose[1])))
    return ("file", len(data) - 1)


async def emit_files_clip(link, suppressor, data) -> tuple:
    """Single-link send decision + echo suppression. ``data`` is
    [(name, raw_bytes, relpath|None), ...] with len >= 2. Returns:
      ("suppressed", 0) -- echo of a just-received set; nothing sent.
      ("files", n)      -- sent all n files as one kind:"files" clip.
      ("file", dropped) -- peer protocol_minor 0; sent the first LOOSE file as
                           a legacy kind:"file" clip; ``dropped`` others not sent.
      ("skipped", 0)    -- peer protocol_minor 0 and the clip is folder-only;
                           nothing sent on this link.
    """
    hashes = [sha256_bytes(bytes(ent[1])) for ent in data]
    aggregate = aggregate_files_hash(hashes)
    if not suppressor.should_send("files", aggregate):
        return ("suppressed", 0)
    return await send_files_to_link(link, data)


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

    async def on_remote_clip(kind: str, data, peer_name: str = "peer") -> None:
        peer = peer_name or "peer"
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
        elif kind == "files":
            assert isinstance(data, list)
            # data: [(name, raw_bytes), ...] already decoded from the wire.
            hashes = [sha256_bytes(bytes(ent[1])) for ent in data]
            aggregate = aggregate_files_hash(hashes)
            suppressor.mark_received("files", aggregate)
            placed = await asyncio.to_thread(watcher.update_local_files, data)
            # Python-macOS places only the FIRST top-level item; a re-detection
            # of a lone placed LOOSE file surfaces as kind:"file", so also seed
            # the single-file suppressor slot with that file's hash. A placed
            # FOLDER re-surfaces as kind:"files" and needs no extra seeding.
            if placed_single_loose_file(data, placed):
                suppressor.mark_received("file", hashes[0])
            log.info(
                f"<- received {len(data)} files from {peer!r} "
                f"({placed} top-level item(s) placed on clipboard)"
            )
            if notify_enabled:
                await notify_async(
                    title=f"AnyClip ← {peer}",
                    message=received_clip_message(data),
                )

    manager = LinkManager(config, node_id, on_remote_clip, max_peers=config.max_peers)

    async def on_local_change(kind: str, data) -> None:
        # Broadcast one local clip to EVERY active link. The watcher and the
        # EchoSuppressor stay global (content-hash based); the should_send
        # check gates the whole broadcast once, before the fan-out.
        if manager.active_count() == 0:
            return
        if kind == "text":
            assert isinstance(data, str)
            if not suppressor.should_send("text", sha256_hex(data)):
                log.debug("skip echo of just-received text")
                return
            sent, skipped = await manager.broadcast_clip("text", data)
            if sent:
                log.info(f"-> sent text {len(data)} chars to {sent} peer(s)")
                if notify_enabled:
                    await notify_async(title="AnyClip →", message=preview(data))
            await notify_size_skips(skipped)
        elif kind == "image":
            assert isinstance(data, (bytes, bytearray))
            png = bytes(data)
            if not suppressor.should_send("image", sha256_bytes(png)):
                log.debug("skip echo of just-received image")
                return
            sent, skipped = await manager.broadcast_clip("image", png)
            if sent:
                log.info(f"-> sent image {len(png)} bytes to {sent} peer(s)")
                if notify_enabled:
                    await notify_async(
                        title="AnyClip →", message=f"image ({len(png)//1024} KB)")
            await notify_size_skips(skipped)
        elif kind == "file":
            assert isinstance(data, tuple) and len(data) == 2
            name, raw = data
            raw_b = bytes(raw)
            if not suppressor.should_send("file", sha256_bytes(raw_b)):
                log.debug("skip echo of just-received file")
                return
            sent, skipped = await manager.broadcast_clip("file", (name, raw_b))
            if sent:
                log.info(
                    f"-> sent file {name!r} {len(raw_b)} bytes to {sent} peer(s)")
                if notify_enabled:
                    await notify_async(
                        title="AnyClip →",
                        message=f"file: {name} ({len(raw_b)//1024} KB)",
                    )
            await notify_size_skips(skipped)
        elif kind == "files":
            assert isinstance(data, list)
            # Global echo check once; per-link minor gating inside the loop.
            hashes = [sha256_bytes(bytes(ent[1])) for ent in data]
            aggregate = aggregate_files_hash(hashes)
            if not suppressor.should_send("files", aggregate):
                log.debug("skip echo of just-received files")
                return
            sent_full, sent_fallback, max_dropped, skipped = (
                await manager.broadcast_files(data)
            )
            total = sent_full + sent_fallback
            if total:
                log.info(
                    f"-> sent files to {total} peer(s) "
                    f"({sent_full} full, {sent_fallback} first-file fallback)"
                )
                if notify_enabled:
                    await notify_async(title="AnyClip →", message=f"{len(data)} files")
            # One aggregated skip toast across ALL peers that could only take
            # the first file (protocol_minor 0). Same principle as the
            # folder-skip aggregation in d8894a0.
            if max_dropped > 0:
                await on_file_skipped(
                    f"{max_dropped} file(s) not sent to {sent_fallback} peer(s) — "
                    "they need an update for multi-file sync"
                )
            await notify_size_skips(skipped)

    async def notify_size_skips(names: list) -> None:
        """At most ONE toast per clip for peers whose 16 MiB receive cap the
        frame would have breached (protocol < 1.2)."""
        message = size_skip_message(names)
        if message is not None:
            await on_file_skipped(message)

    async def on_file_skipped(message: str) -> None:
        if notify_enabled:
            await notify_async(title="AnyClip", message=message)

    watcher = ClipboardWatcher(
        config.poll_interval, on_local_change,
        on_file_skipped=on_file_skipped,
    )
    beacon = MdnsBeacon(config, node_id, manager.ensure_link)
    manager.attach_beacon(beacon)

    log.info(f"AnyClip starting (node {node_id[:8]}, name={config.name!r})")
    if config.peers:
        log.info(f"manual peers: {[f'{h}:{p}' for h, p in config.peers]}")
    tasks: list[asyncio.Task] = []
    try:
        await beacon.start()
        coros = [
            manager.serve(),
            manager.apply_loop(),
            watcher.run(),
            mdns_reconnect_loop(beacon, manager),
            network_watchdog(beacon),
            idle_link_watchdog(beacon, manager),
        ]
        # The per-link staleness dropper (link_ping_loop) is now started
        # per link inside LinkManager._serve_link, not once globally here.
        for host, port in config.peers:
            coros.append(peer_keepalive(host, port, manager))
        if sys.platform == "darwin":
            coros.append(_run_permission_probe(beacon))
        tasks = [asyncio.create_task(c) for c in coros]
        await asyncio.gather(*tasks)
    finally:
        # Cancel any siblings still running and *await* them so their
        # finally blocks complete before the event loop closes. Without
        # this, asyncio.gather() re-raises on the first failure without
        # draining peers, leaving Windows ProactorEventLoop's internal
        # IocpProactor.accept coroutine pending -- which surfaces as
        # "Task was destroyed but it is pending!" at loop shutdown.
        for t in tasks:
            if not t.done():
                t.cancel()
        if tasks:
            await asyncio.gather(*tasks, return_exceptions=True)
        await manager.close()
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
