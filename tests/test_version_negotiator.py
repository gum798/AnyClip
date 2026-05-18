"""Table-driven tests for the version_negotiator pure module.

Nine canonical cases cover the (local major, peer major) x (local minor,
peer minor) matrix plus an app_version sanity case.
"""

from __future__ import annotations

import pytest

from version_negotiator import (
    Compatibility,
    VersionInfo,
    link_allowed,
    negotiate,
)


def _v(app: str, major: int, minor: int) -> VersionInfo:
    return VersionInfo(app_version=app, protocol_major=major, protocol_minor=minor)


@pytest.mark.parametrize(
    "local,peer,expected",
    [
        # 1. identical -> compatible
        (_v("1.0.0", 1, 0), _v("1.0.0", 1, 0), Compatibility.COMPATIBLE),
        # 2. peer minor older (same major) -> PeerOlderMinor (link kept)
        (_v("1.0.0", 1, 2), _v("1.0.0", 1, 1), Compatibility.PEER_OLDER_MINOR),
        # 3. peer minor newer (same major) -> PeerNewerMinor (link kept)
        (_v("1.0.0", 1, 1), _v("1.0.0", 1, 2), Compatibility.PEER_NEWER_MINOR),
        # 4. peer major older -> PeerOlderMajor (refuse)
        (_v("2.0.0", 2, 0), _v("1.5.0", 1, 9), Compatibility.PEER_OLDER_MAJOR),
        # 5. peer major newer -> PeerNewerMajor (refuse)
        (_v("1.0.0", 1, 9), _v("2.0.0", 2, 0), Compatibility.PEER_NEWER_MAJOR),
        # 6. peer major older but minor newer -> major dominates
        (_v("2.0.0", 2, 0), _v("1.9.0", 1, 99), Compatibility.PEER_OLDER_MAJOR),
        # 7. peer major newer but minor older -> major dominates
        (_v("1.99.0", 1, 99), _v("2.0.0", 2, 0), Compatibility.PEER_NEWER_MAJOR),
        # 8. different app_version, same protocol -> compatible
        (_v("1.0.0", 1, 0), _v("1.4.7", 1, 0), Compatibility.COMPATIBLE),
        # 9. empty peer app_version, same protocol -> compatible
        (_v("1.0.0", 1, 0), _v("", 1, 0), Compatibility.COMPATIBLE),
    ],
)
def test_negotiate_matrix(local: VersionInfo, peer: VersionInfo, expected: Compatibility) -> None:
    assert negotiate(local, peer) == expected


@pytest.mark.parametrize(
    "result,allowed",
    [
        (Compatibility.COMPATIBLE, True),
        (Compatibility.PEER_OLDER_MINOR, True),
        (Compatibility.PEER_NEWER_MINOR, True),
        (Compatibility.PEER_OLDER_MAJOR, False),
        (Compatibility.PEER_NEWER_MAJOR, False),
    ],
)
def test_link_allowed(result: Compatibility, allowed: bool) -> None:
    assert link_allowed(result) is allowed


def test_negotiate_is_symmetric_for_major_mismatch() -> None:
    a = _v("1.0.0", 1, 0)
    b = _v("2.0.0", 2, 0)
    assert negotiate(a, b) == Compatibility.PEER_NEWER_MAJOR
    assert negotiate(b, a) == Compatibility.PEER_OLDER_MAJOR
    assert not link_allowed(negotiate(a, b))
    assert not link_allowed(negotiate(b, a))


def test_backward_compat_default_minor_zero() -> None:
    """Old peer sending only PROTOCOL_VERSION (no minor) defaults to minor=0.

    Local at (1, 0) talking to legacy peer parsed as (1, 0) must be
    COMPATIBLE -- this is the upgrade path so existing users do not
    break the moment one side adopts the extended handshake.
    """
    local = _v("1.0.0", 1, 0)
    legacy_peer = _v("0.0.0", 1, 0)  # legacy peer, default minor=0
    assert negotiate(local, legacy_peer) == Compatibility.COMPATIBLE
