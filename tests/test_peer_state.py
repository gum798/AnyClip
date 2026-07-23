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
    linked_names,
    reduce,
    status_label,
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
        LinkUp(node_id="abcd1234", peer_name="peer-1",
               app_version="1.3.0", protocol_minor=1),
    ], now=42.0)
    assert final.kind == "linked"
    assert final.peers == {"abcd1234": "peer-1"}
    assert final.since == 42.0


def test_linkup_then_linkdown_goes_to_searching() -> None:
    final = _fold([
        LinkUp(node_id="abcd1234", peer_name="peer-1",
               app_version="1.3.0", protocol_minor=1),
        LinkDown(node_id="abcd1234", reason="peer disconnected"),
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
    state = reduce(state, LinkUp(node_id="i", peer_name="p",
                                 app_version="1.3.0", protocol_minor=1), now=2.0)
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
    state = reduce(INITIAL, LinkUp(node_id="i", peer_name="p",
                                   app_version="1.3.0", protocol_minor=1), now=1.0)
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
        LinkUp(node_id="i", peer_name="p", app_version="1.3.0", protocol_minor=1),
        LinkDown(node_id="i", reason="bye"),
        PeerDiscovered(name="p", addr="x"),
        LinkUp(node_id="i", peer_name="p", app_version="1.3.0", protocol_minor=1),
    ]
    a = _fold(seq, now=10.0)
    b = _fold(seq, now=10.0)
    assert a == b


@pytest.mark.parametrize("threshold", [HANDSHAKE_FAIL_THRESHOLD])
def test_threshold_constant_is_five(threshold: int) -> None:
    """Slice AC pins the threshold to 5 handshake failures."""
    assert threshold == 5


def _up(node_id: str, name: str) -> LinkUp:
    return LinkUp(node_id=node_id, peer_name=name,
                  app_version="1.3.0", protocol_minor=1)


def test_two_linkups_track_both_peers() -> None:
    final = _fold([_up("id-a", "alice"), _up("id-b", "bob")], now=5.0)
    assert final.kind == "linked"
    assert final.peers == {"id-a": "alice", "id-b": "bob"}
    assert final.since == 5.0  # first link sets since; second keeps it


def test_handshake_fails_do_not_trip_error_while_a_peer_is_linked() -> None:
    # An established link masks the auth escalation: a stranger failing auth
    # must not flip a working multi-peer UI into error (parity with Swift's
    # handshakeFailsDoNotTripErrorWhileAPeerIsLinked). The counter may climb
    # past the threshold while linked, but the state stays linked, peers intact.
    state = _fold([_up("id-a", "alice"), _up("id-b", "bob")], now=5.0)
    for i in range(HANDSHAKE_FAIL_THRESHOLD + 2):
        state = reduce(state, HandshakeFailed(addr="a", reason="auth"), now=float(i))
    assert state.kind == "linked"
    assert state.peers == {"id-a": "alice", "id-b": "bob"}


def test_second_linkup_keeps_first_since() -> None:
    state = reduce(INITIAL, _up("id-a", "alice"), now=5.0)
    state = reduce(state, _up("id-b", "bob"), now=9.0)
    assert state.since == 5.0


def test_linkdown_removes_only_that_peer_and_stays_linked() -> None:
    state = _fold([_up("id-a", "alice"), _up("id-b", "bob")], now=5.0)
    state = reduce(state, LinkDown(node_id="id-a", reason="gone"), now=6.0)
    assert state.kind == "linked"
    assert state.peers == {"id-b": "bob"}


def test_linkdown_last_peer_goes_to_searching() -> None:
    state = _fold([_up("id-a", "alice")], now=5.0)
    state = reduce(state, LinkDown(node_id="id-a", reason="bye"), now=6.0)
    assert state.kind == "searching"
    assert state.reason == "bye"
    assert state.peers == {}


def test_linkdown_unknown_node_id_is_noop() -> None:
    state = _fold([_up("id-a", "alice")], now=5.0)
    after = reduce(state, LinkDown(node_id="id-x", reason="stale"), now=6.0)
    assert after == state


def test_linked_names_sorted_ordinally() -> None:
    state = _fold([_up("id-1", "Zoe"), _up("id-2", "amy"), _up("id-3", "Bob")], now=1.0)
    # Ordinal (code-point) order: uppercase before lowercase.
    assert linked_names(state) == ["Bob", "Zoe", "amy"]


def test_status_label_linked_lists_sorted_peers() -> None:
    state = _fold([_up("id-1", "win-pc"), _up("id-2", "macbook")], now=1.0)
    assert status_label(state) == "Linked: macbook, win-pc"


def test_status_label_non_linked_states() -> None:
    assert status_label(INITIAL) == "Idle"
    assert status_label(State(kind="searching")) == "Searching for peer"
    assert status_label(State(kind="error", reason="local_network")) == \
        "Error: local_network"
    assert status_label(State(kind="error")) == "Error: unknown"
