"""Heartbeat liveness-deadline tests for link_ping_loop.

Regression for the half-open zombie link: a peer that slept or vanished
without RST/FIN keeps the socket writable (pings never error) but sends
nothing back. link_ping_loop must detect the inbound silence and drop the
link so the reconnect loop can run, rather than idling forever.
"""
import asyncio

import anyclip


class FakeLink:
    """Duck-typed stand-in for PeerLink exposing only what link_ping_loop uses."""

    def __init__(self, idle_seconds: float) -> None:
        self.active = True
        self._idle = idle_seconds
        self.pings = 0
        self.dropped: float | None = None

    async def send_ping(self) -> None:
        self.pings += 1

    def seconds_since_inbound(self):
        return self._idle

    def drop_stale_link(self, idle_seconds: float) -> None:
        self.dropped = idle_seconds


async def _run_loop_briefly(link, interval, dead_factor):
    task = asyncio.create_task(
        anyclip.link_ping_loop(link, interval=interval, dead_factor=dead_factor)
    )
    await asyncio.sleep(interval * 2.5)
    task.cancel()
    try:
        await task
    except asyncio.CancelledError:
        pass


def test_link_ping_loop_drops_silent_peer():
    # interval 0.05s, dead_factor 3 -> deadline 0.15s; idle 10s is way past it.
    link = FakeLink(idle_seconds=10.0)
    asyncio.run(_run_loop_briefly(link, interval=0.05, dead_factor=3))
    assert link.pings >= 1            # still pinging
    assert link.dropped == 10.0       # and dropped the stale link


def test_link_ping_loop_keeps_responsive_peer():
    # idle 0.01s stays well under the 0.15s deadline -> never dropped.
    link = FakeLink(idle_seconds=0.01)
    asyncio.run(_run_loop_briefly(link, interval=0.05, dead_factor=3))
    assert link.pings >= 1
    assert link.dropped is None


def test_seconds_since_inbound_none_when_unlinked():
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = None  # not linked
    assert link.seconds_since_inbound() is None


def test_drop_stale_link_noop_when_unlinked():
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = None
    # Must not raise when there is no active writer.
    link.drop_stale_link(99.0)
