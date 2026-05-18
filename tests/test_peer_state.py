"""Golden tests for the peer_state reducer.

Each scenario is "feed this exact event sequence into INITIAL and
assert the final state.kind/reason/peer_name". The reducer is pure so
the assertions are stable.
"""

from __future__ import annotations

from typing import List

import pytest

from peer_state import (
    HANDSHAKE_FAIL_THRESHOLD,
    INITIAL,
    DaemonEvent,
    HandshakeFailed,
    LinkDown,
    LinkUp,
    PeerDiscovered,
    PermissionMissing,
    State,
    reduce,
)


def _fold(events: List[DaemonEvent], now: float = 100.0) -> State:
    state = INITIAL
    for e in events:
        state = reduce(state, e, now=now)
    return state


def test_initial_is_idle() -> None:
    assert INITIAL.kind == "idle"


def test_discovered_then_linkup() -> None:
    final = _fold([
        PeerDiscovered(name="peer-1", addr="10.0.0.2:24816"),
        LinkUp(peer_name="peer-1", peer_id="abcd1234"),
    ], now=42.0)
    assert final.kind == "linked"
    assert final.peer_name == "peer-1"
    assert final.since == 42.0


def test_linkup_then_linkdown_goes_to_searching() -> None:
    final = _fold([
        LinkUp(peer_name="peer-1", peer_id="abcd1234"),
        LinkDown(reason="peer disconnected"),
    ])
    assert final.kind == "searching"
    assert final.reason == "peer disconnected"


def test_five_handshake_failures_become_error_auth() -> None:
    events = [HandshakeFailed(addr="10.0.0.2", reason="auth")] * HANDSHAKE_FAIL_THRESHOLD
    final = _fold(events)
    assert final.kind == "error"
    assert final.reason == "auth"


def test_four_handshake_failures_do_not_yet_error() -> None:
    events = [HandshakeFailed(addr="10.0.0.2", reason="auth")] * (HANDSHAKE_FAIL_THRESHOLD - 1)
    final = _fold(events)
    assert final.kind != "error"
    assert final.consecutive_handshake_fails == HANDSHAKE_FAIL_THRESHOLD - 1


def test_linkup_resets_handshake_failure_counter() -> None:
    state = INITIAL
    for _ in range(HANDSHAKE_FAIL_THRESHOLD - 1):
        state = reduce(state, HandshakeFailed(addr="x", reason="auth"), now=1.0)
    assert state.consecutive_handshake_fails == HANDSHAKE_FAIL_THRESHOLD - 1
    state = reduce(state, LinkUp(peer_name="p", peer_id="i"), now=2.0)
    assert state.kind == "linked"
    assert state.consecutive_handshake_fails == 0
    # Now another failure should NOT immediately trip into error.
    state = reduce(state, HandshakeFailed(addr="x", reason="auth"), now=3.0)
    assert state.kind != "error"


def test_permission_missing_immediately_errors() -> None:
    final = _fold([PermissionMissing(kind="local_network")])
    assert final.kind == "error"
    assert final.reason == "local_network"


def test_permission_missing_kind_no_network() -> None:
    final = _fold([PermissionMissing(kind="no_network")])
    assert final.reason == "no_network"


def test_discovered_while_linked_is_noop() -> None:
    state = reduce(INITIAL, LinkUp(peer_name="p", peer_id="i"), now=1.0)
    after = reduce(state, PeerDiscovered(name="p", addr="x"), now=2.0)
    assert after == state  # no state change


def test_discovered_from_error_goes_back_to_searching() -> None:
    state = reduce(INITIAL, PermissionMissing(kind="local_network"), now=1.0)
    assert state.kind == "error"
    after = reduce(state, PeerDiscovered(name="p", addr="x"), now=2.0)
    assert after.kind == "searching"


def test_reducer_is_pure_for_identical_sequence() -> None:
    seq = [
        PeerDiscovered(name="p", addr="x"),
        LinkUp(peer_name="p", peer_id="i"),
        LinkDown(reason="bye"),
        PeerDiscovered(name="p", addr="x"),
        LinkUp(peer_name="p", peer_id="i"),
    ]
    a = _fold(seq, now=10.0)
    b = _fold(seq, now=10.0)
    assert a == b


@pytest.mark.parametrize("threshold", [HANDSHAKE_FAIL_THRESHOLD])
def test_threshold_constant_is_five(threshold: int) -> None:
    """Slice AC pins the threshold to 5 handshake failures."""
    assert threshold == 5
