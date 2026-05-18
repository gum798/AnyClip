"""Pure version negotiation between two AnyClip peers.

Decides whether two instances can link based on their app + protocol
versions. Kept side-effect free so it can be unit tested with the table
of cases in tests/test_version_negotiator.py and so the daemon code in
anyclip.py only needs to consume the resulting enum.
"""

from __future__ import annotations

from dataclasses import dataclass
from enum import Enum


class Compatibility(Enum):
    COMPATIBLE = "compatible"
    PEER_OLDER_MINOR = "peer_older_minor"
    PEER_NEWER_MINOR = "peer_newer_minor"
    PEER_OLDER_MAJOR = "peer_older_major"
    PEER_NEWER_MAJOR = "peer_newer_major"


@dataclass(frozen=True)
class VersionInfo:
    app_version: str
    protocol_major: int
    protocol_minor: int


def negotiate(local: VersionInfo, peer: VersionInfo) -> Compatibility:
    """Compare local vs peer protocol versions.

    Major version dominates: any major mismatch is a refusal regardless
    of minor. Minor differences are advisory and keep the link.
    ``app_version`` is informational and never affects the outcome.
    """
    if peer.protocol_major < local.protocol_major:
        return Compatibility.PEER_OLDER_MAJOR
    if peer.protocol_major > local.protocol_major:
        return Compatibility.PEER_NEWER_MAJOR
    if peer.protocol_minor < local.protocol_minor:
        return Compatibility.PEER_OLDER_MINOR
    if peer.protocol_minor > local.protocol_minor:
        return Compatibility.PEER_NEWER_MINOR
    return Compatibility.COMPATIBLE


def link_allowed(result: Compatibility) -> bool:
    """True if the link should be kept open for this compatibility result."""
    return result in {
        Compatibility.COMPATIBLE,
        Compatibility.PEER_OLDER_MINOR,
        Compatibility.PEER_NEWER_MINOR,
    }
