"""Daemon-event types and pure state-machine reducer for the UI shell.

`anyclip.py` emits `DaemonEvent`s as link milestones happen. A GUI shell
(menubar/tray) subscribes to that stream and folds it through
`reduce()` to produce the user-visible state: Idle / Searching /
Linked(peer, since) / Error(reason).

Keeping the reducer pure means every UI transition is reproducible
from an event log -- which is what the golden tests in
tests/test_peer_state.py rely on.
"""

from __future__ import annotations

from dataclasses import dataclass, field, replace
from typing import Mapping, Optional, Union


# ---- Events ---------------------------------------------------------------


@dataclass(frozen=True)
class PeerDiscovered:
    name: str
    addr: str


@dataclass(frozen=True)
class LinkUp:
    node_id: str      # stable per-session peer identity (fresh UUID per start)
    peer_name: str    # display name from the peer's hello
    app_version: str  # peer's advertised app version
    protocol_minor: int


@dataclass(frozen=True)
class LinkDown:
    node_id: str      # which peer dropped; the reducer removes only this one
    reason: str


@dataclass(frozen=True)
class HandshakeFailed:
    addr: str
    reason: str  # "auth" | "timeout" | "version" | ...


@dataclass(frozen=True)
class PermissionMissing:
    kind: str  # "local_network" | "no_network" | ...


DaemonEvent = Union[
    PeerDiscovered,
    LinkUp,
    LinkDown,
    HandshakeFailed,
    PermissionMissing,
]


# ---- State ----------------------------------------------------------------


@dataclass(frozen=True)
class State:
    """Visible UI state plus a single internal counter.

    The counter (`consecutive_handshake_fails`) is internal bookkeeping
    that lets the reducer stay pure while still being able to trip into
    Error("auth") after a run of failed handshakes. UI code should look
    only at `kind`, `peers`, `since`, `reason` (use `linked_names` /
    `status_label` for display).

    `peers` maps node_id -> display name; it is the full mesh membership.
    `kind == "linked"` iff `peers` is non-empty.
    """

    kind: str  # "idle" | "searching" | "linked" | "error"
    peers: Mapping[str, str] = field(default_factory=dict)
    since: Optional[float] = None
    reason: Optional[str] = None
    consecutive_handshake_fails: int = 0


INITIAL: State = State(kind="idle")
HANDSHAKE_FAIL_THRESHOLD: int = 5


# ---- Reducer --------------------------------------------------------------


def reduce(prev: State, event: DaemonEvent, now: float) -> State:
    """Fold one event into the previous state. Pure.

    `now` is supplied by the caller (real wall clock in production,
    fake clock in tests) so the resulting `Linked.since` is
    deterministic in tests.
    """
    if isinstance(event, PermissionMissing):
        return State(kind="error", reason=event.kind)

    if isinstance(event, LinkUp):
        # Insert/update this peer by node_id. `since` marks when we FIRST
        # became linked (empty -> non-empty); joining peers keep it.
        peers = dict(prev.peers)
        peers[event.node_id] = event.peer_name
        return State(
            kind="linked",
            peers=peers,
            since=now if not prev.peers else prev.since,
            consecutive_handshake_fails=0,
        )

    if isinstance(event, LinkDown):
        # Remove ONLY this peer. Still linked to others -> stay linked.
        # Last peer gone -> back to Searching (mDNS re-announces within a
        # few seconds, so Searching is the right idle-but-active parking
        # state); an unknown node_id is a no-op (already removed).
        if event.node_id not in prev.peers:
            return prev
        peers = dict(prev.peers)
        del peers[event.node_id]
        if peers:
            return replace(prev, peers=peers)
        return State(kind="searching", reason=event.reason)

    if isinstance(event, PeerDiscovered):
        # Discovery only moves us out of Idle/Error. If we are already
        # Searching or Linked, discovery is informational -- do not
        # flap state on every mDNS re-advertisement.
        if prev.kind in ("idle", "error"):
            return State(kind="searching")
        return prev

    if isinstance(event, HandshakeFailed):
        new_count = prev.consecutive_handshake_fails + 1
        # An established link masks the auth escalation: one stranger failing
        # auth must not flip a working multi-peer UI into error. While linked
        # the counter still increments, but escalation waits until NO peer is
        # linked (parity with Swift PeerState + C# PeerStateReducer).
        if new_count >= HANDSHAKE_FAIL_THRESHOLD and not prev.peers:
            return State(
                kind="error",
                reason="auth",
                consecutive_handshake_fails=new_count,
            )
        return replace(prev, consecutive_handshake_fails=new_count)

    return prev


# ---- Display helpers (shared by both GUI shells) --------------------------


def linked_names(state: State) -> list:
    """Peer display names for a linked state, sorted ordinally (code-point
    order). Empty when not linked. The shells join these with ', '."""
    return sorted(state.peers.values())


def status_label(state: State) -> str:
    """Single-line status text shared by the macOS menubar and Windows tray.

    Linked -> 'Linked: a, b' (names sorted ordinally); otherwise the
    searching / error / idle wording both shells used before the mesh.
    """
    if state.kind == "linked":
        return "Linked: " + ", ".join(linked_names(state))
    if state.kind == "searching":
        return "Searching for peer"
    if state.kind == "error":
        return f"Error: {state.reason or 'unknown'}"
    return "Idle"
