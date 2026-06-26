"""Send-timeout tests.

Regression for the Mac outbound wedge: a clipboard send (and the heartbeat
ping) parked on a lost send completion / full TCP buffer of a half-open
socket. Because the clipboard poll loop and the heartbeat loop ``await`` the
send inline, that parked send froze BOTH -- clipboard detection stopped and
the v1.1.7 self-heal could never fire, so the link stayed "connected" yet copy
silently stopped until an app restart.

The fix: every app-initiated send is bounded by a timeout. A send that does
not drain within the budget closes the writer, which makes the next recv
return EOF and lets the reconnect loop take over.
"""
import asyncio

import anyclip


class HangingWriter:
    """StreamWriter stand-in whose drain() never completes (full TCP buffer /
    half-open peer) until the writer is closed."""

    def __init__(self) -> None:
        self.closed = False
        self._buf = bytearray()

    def write(self, data) -> None:
        self._buf += data

    async def drain(self) -> None:
        # Park like a real drain() on a wedged socket; only close() ends it.
        while not self.closed:
            await asyncio.sleep(0.01)

    def is_closing(self) -> bool:
        return self.closed

    def close(self) -> None:
        self.closed = True


def _bare_link(send_timeout: float) -> "anyclip.PeerLink":
    link = anyclip.PeerLink.__new__(anyclip.PeerLink)
    link._writer = None
    link._send_timeout = send_timeout
    return link


def test_send_times_out_and_closes_wedged_writer():
    async def go():
        link = _bare_link(send_timeout=0.05)
        w = HangingWriter()
        # _send must return within its own timeout, not hang forever. The
        # generous outer guard only catches a true hang (the bug).
        try:
            await asyncio.wait_for(
                link._send(w, {"type": "ping", "ts": 0.0}), timeout=2.0
            )
        except asyncio.TimeoutError:
            raise AssertionError("_send hung: write not bounded by a timeout")
        assert w.closed, "wedged writer must be closed to force reconnect"

    asyncio.run(go())


def test_send_does_not_close_on_fast_drain():
    class FastWriter(HangingWriter):
        async def drain(self) -> None:  # completes immediately
            return

    async def go():
        link = _bare_link(send_timeout=0.05)
        w = FastWriter()
        await asyncio.wait_for(
            link._send(w, {"type": "ping", "ts": 0.0}), timeout=2.0
        )
        assert not w.closed, "a healthy send must leave the link intact"

    asyncio.run(go())
