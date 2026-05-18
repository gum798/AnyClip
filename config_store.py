"""Persistent on-disk store for AnyClip's shared secret token.

Kept side-effect minimal: `load()` only reads the JSON file at
~/.anyclip/config.json, `save()` writes it atomically with 0600
permissions, `generate_token()` produces a high-entropy URL-safe
string. The dataclass deliberately holds only what needs to outlive
process restarts -- runtime knobs (port, peer, verbose, ...) stay in
anyclip.Config and stay on the command line.
"""

from __future__ import annotations

import json
import logging
import os
import secrets
import sys
import tempfile
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Optional

log = logging.getLogger("anyclip.config_store")

# Override-able for tests. Production callers should not touch this.
DEFAULT_CONFIG_DIR = Path.home() / ".anyclip"
CONFIG_FILENAME = "config.json"
# 32 bytes of entropy -> ~43 char URL-safe string. Comfortably above
# what brute-force over LAN can cover, and human-paste friendly.
TOKEN_ENTROPY_BYTES = 32


@dataclass
class Config:
    """What we persist between runs. Only token for now (slice-02 scope)."""
    token: str


def config_path(config_dir: Optional[Path] = None) -> Path:
    return (config_dir or DEFAULT_CONFIG_DIR) / CONFIG_FILENAME


def generate_token() -> str:
    """Return a fresh URL-safe token with `TOKEN_ENTROPY_BYTES` of entropy."""
    return secrets.token_urlsafe(TOKEN_ENTROPY_BYTES)


def load(config_dir: Optional[Path] = None) -> Optional[Config]:
    """Read the config file. Return None if missing or unreadable/corrupt.

    Corruption is treated as "no config" so a damaged file never blocks
    startup; the user can then re-save and overwrite it.
    """
    path = config_path(config_dir)
    try:
        raw = path.read_text(encoding="utf-8")
    except FileNotFoundError:
        return None
    except OSError as exc:
        log.warning(f"config read failed: {exc}")
        return None
    try:
        data = json.loads(raw)
    except json.JSONDecodeError as exc:
        log.warning(f"config corrupt, ignoring ({exc})")
        return None
    if not isinstance(data, dict):
        log.warning("config not a JSON object, ignoring")
        return None
    token = data.get("token")
    if not isinstance(token, str) or not token:
        log.warning("config missing token, ignoring")
        return None
    return Config(token=token)


def save(config: Config, config_dir: Optional[Path] = None) -> None:
    """Atomically write the config with 0600 permissions.

    Atomicity: write to a same-directory temp file, fsync, then
    os.replace. Permissions: chmod 0600 before the replace so the
    target never exists in a more-permissive mode. On Windows the
    chmod call is best-effort (NTFS ACLs ignore POSIX modes).
    """
    target_dir = config_dir or DEFAULT_CONFIG_DIR
    target_dir.mkdir(parents=True, exist_ok=True)
    target = target_dir / CONFIG_FILENAME
    payload = json.dumps(asdict(config), indent=2, sort_keys=True) + "\n"

    fd, tmp_name = tempfile.mkstemp(
        prefix=".config.json.", suffix=".tmp", dir=str(target_dir)
    )
    tmp_path = Path(tmp_name)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as fh:
            fh.write(payload)
            fh.flush()
            os.fsync(fh.fileno())
        # 0600 = rw for owner only. No-op on Windows where chmod only
        # toggles the read-only bit; we accept that as a known platform
        # limitation rather than re-implementing ACLs here.
        if sys.platform != "win32":
            os.chmod(tmp_path, 0o600)
        os.replace(tmp_path, target)
    except Exception:
        # Best-effort cleanup of the temp file on any failure path.
        try:
            tmp_path.unlink()
        except OSError:
            pass
        raise
