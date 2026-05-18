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

from dataclasses import dataclass, replace
from typing import Optional, Union


# ---- Events ---------------------------------------------------------------


@dataclass(frozen=True)
class PeerDiscovered:
    name: str
    addr: str


@dataclass(frozen=True)
class LinkUp:
    peer_name: str
    peer_id: str


@dataclass(frozen=True)
class LinkDown:
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
    only at `kind`, `peer_name`, `since`, `reason`.
    """

    kind: str  # "idle" | "searching" | "linked" | "error"
    peer_name: Optional[str] = None
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
        return State(
            kind="linked",
            peer_name=event.peer_name,
            since=now,
            consecutive_handshake_fails=0,
        )

    if isinstance(event, LinkDown):
        # When a peer drops we go back to actively looking. Earlier
        # PeerDiscovered events may be stale, but mDNS will re-announce
        # within a few seconds so Searching is the right idle-but-active
        # parking state.
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
        if new_count >= HANDSHAKE_FAIL_THRESHOLD:
            return State(
                kind="error",
                reason="auth",
                consecutive_handshake_fails=new_count,
            )
        return replace(prev, consecutive_handshake_fails=new_count)

    return prev
