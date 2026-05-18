"""Startup self-diagnosis for the macOS Local Network permission.

On macOS the user can silently revoke a daemon's Local Network entitlement
in System Settings; mDNS then runs but advertises into a vacuum and
receives nothing, so the daemon looks 'stuck searching' forever. This
module judges that situation after a short observation window:

- 0 mDNS events in the first ``wait_seconds`` (default 30) AND there
  *is* a network interface => `BLOCKED_LOCAL_NETWORK`.
- No usable network interface at all => `NO_NETWORK`.
- Otherwise => `OK`.

Kept pure-ish: the decision function is fully pure and table-tested;
the async `probe()` only adds an injectable sleep so tests can wind the
clock forward without real time passing.

Windows has no Local Network permission concept (firewall is handled by
the OS dialog), so the daemon must skip wiring this probe on Windows.
That decision is made by the caller, not here.
"""

from __future__ import annotations

import asyncio
from enum import Enum
from typing import Awaitable, Callable, Optional


class Result(Enum):
    OK = "ok"
    BLOCKED_LOCAL_NETWORK = "blocked_local_network"
    NO_NETWORK = "no_network"


def decide(events_seen: int, has_network: bool) -> Result:
    """Pure judgment for one observation window."""
    if not has_network:
        return Result.NO_NETWORK
    if events_seen <= 0:
        return Result.BLOCKED_LOCAL_NETWORK
    return Result.OK


async def probe(
    events_seen_fn: Callable[[], int],
    has_network_fn: Callable[[], bool],
    wait_seconds: float = 30.0,
    sleep_fn: Optional[Callable[[float], Awaitable[None]]] = None,
) -> Result:
    """Wait for the observation window then judge.

    ``sleep_fn`` defaults to ``asyncio.sleep`` but can be replaced in
    tests with an instant-return coroutine so the matrix runs in
    microseconds instead of half a minute.
    """
    sleep = sleep_fn or asyncio.sleep
    await sleep(wait_seconds)
    return decide(events_seen_fn(), has_network_fn())
