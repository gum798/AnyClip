"""LinkManager unit tests: routing (new/known node_id), cap, replacement +
tie-break, broadcast fan-out with per-link failure isolation, per-link minor
gating, serialized receive-apply, and an end-to-end two-manager handshake +
broadcast over loopback. Uses the repo's asyncio.run(go()) test pattern."""
from __future__ import annotations

import asyncio
import socket
import types

import anyclip
from anyclip import LinkManager


def _cfg(name="me", port=0):
    return types.SimpleNamespace(token="tok", name=name, port=port)


def _free_port() -> int:
    s = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()
    return port


class FakeWriter:
    """StreamWriter stand-in for _route/broadcast tests (never written to)."""
    def __init__(self):
        self.closed = False
    def is_closing(self):
        return self.closed
    def close(self):
        self.closed = True
    def get_extra_info(self, _key):
        return None


class FakeLink:
    """Duck-typed PeerLink for broadcast tests."""
    def __init__(self, minor=1, fail=False, name="peer", addr=None):
        self.peer_protocol_minor = minor
        self.peer_name = name
        self.remote_addr = addr
        self.active = True
        self.sent = []
        self.closed = False
        self._fail = fail
    async def send_clip(self, kind, content):
        if self._fail:
            raise ConnectionError("boom")
        self.sent.append((kind, content))
    async def close(self):
        self.closed = True
        self.active = False


async def _route(mgr, peer_id, inbound=True, name="peer", minor=1, addr=None):
    return await mgr._route(peer_id, name, minor, "1.3.0",
                            None, FakeWriter(), inbound, addr)


# ---- routing / cap / replacement ---------------------------------------

def test_new_node_id_creates_link():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        link = await _route(mgr, "peer-1")
        assert link is not None
        assert list(mgr._links) == ["peer-1"]
        assert mgr.active_count() == 1
    asyncio.run(go())


def test_known_node_id_replaces_live_session_post_race():
    async def go():
        import time as _t
        mgr = LinkManager(_cfg(), "node-self", None)
        old_writer = FakeWriter()
        first = await mgr._route("peer-1", "old", 1, "1.3.0",
                                 None, old_writer, True, None)
        first._linked_at = _t.monotonic() - 10  # past the race window
        second = await _route(mgr, "peer-1", name="new")
        assert second is not None and second is not first
        assert old_writer.closed  # old socket closed on replacement
        assert mgr._links["peer-1"] is second
        assert mgr.active_count() == 1  # replaced, not doubled
    asyncio.run(go())


def test_tiebreak_drops_losing_new_connection_in_race_window():
    async def go():
        # self node_id "aaaa" < peer "zzzz": on an INBOUND duplicate during
        # the race window the local side keeps its own inbound end only when
        # node_id > peer, so "aaaa" inbound must DROP the new connection.
        mgr = LinkManager(_cfg(), "aaaa", None)
        first = await _route(mgr, "zzzz", inbound=True, name="orig")
        # first._linked_at is "now" -> inside RACE_WINDOW_S.
        loser = await _route(mgr, "zzzz", inbound=True, name="dup")
        assert loser is None
        assert mgr._links["zzzz"] is first  # original kept
    asyncio.run(go())


def test_cap_refuses_new_but_routes_known_reconnect():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None, max_peers=1)
        a = await _route(mgr, "peer-a")
        assert a is not None
        # New node_id at cap -> refused.
        b = await _route(mgr, "peer-b")
        assert b is None
        assert list(mgr._links) == ["peer-a"]
        # Known node_id reconnect at cap -> routed (replacement), never refused.
        import time as _t
        a._linked_at = _t.monotonic() - 10
        a2 = await _route(mgr, "peer-a")
        assert a2 is not None and a2 is not a
        assert list(mgr._links) == ["peer-a"]
    asyncio.run(go())


def test_dead_link_removal_frees_a_cap_slot():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None, max_peers=1)
        await _route(mgr, "peer-a")
        assert await _route(mgr, "peer-b") is None  # at cap
        del mgr._links["peer-a"]  # simulate _serve_link teardown
        assert await _route(mgr, "peer-b") is not None  # slot freed
    asyncio.run(go())


# ---- broadcast ---------------------------------------------------------

def test_broadcast_clip_fans_out_and_isolates_failure():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        good = FakeLink(name="good")
        bad = FakeLink(name="bad", fail=True)
        mgr._links = {"g": good, "b": bad}
        await mgr.broadcast_clip("text", "hello")
        assert good.sent == [("text", "hello")]
        assert bad.closed  # failed link dropped, others unaffected
    asyncio.run(go())


def test_broadcast_files_per_link_minor_gating():
    async def go():
        mgr = LinkManager(_cfg(), "node-self", None)
        new_peer = FakeLink(minor=1, name="new")
        old_peer = FakeLink(minor=0, name="old")
        mgr._links = {"n": new_peer, "o": old_peer}
        data = [("a.txt", b"one"), ("b.txt", b"two"), ("c.txt", b"three")]
        full, fallback, dropped = await mgr.broadcast_files(data)
        assert full == 1 and fallback == 1 and dropped == 2
        assert new_peer.sent == [("files", data)]
        assert old_peer.sent == [("file", ("a.txt", b"one"))]
    asyncio.run(go())


# ---- serialized apply --------------------------------------------------

def test_apply_loop_serializes_across_links():
    async def go():
        order = []
        async def handler(kind, data, peer):
            order.append(("enter", data))
            await asyncio.sleep(0.01)
            order.append(("exit", data))
        mgr = LinkManager(_cfg(), "node-self", handler)
        task = asyncio.create_task(mgr.apply_loop())
        await mgr._enqueue_received("text", "A", "p1")
        await mgr._enqueue_received("text", "B", "p2")
        await asyncio.sleep(0.06)
        task.cancel()
        # No interleaving: A fully applied before B starts.
        assert order == [("enter", "A"), ("exit", "A"),
                         ("enter", "B"), ("exit", "B")]
    asyncio.run(go())


# ---- end-to-end handshake + broadcast over loopback --------------------

def test_two_managers_handshake_link_and_broadcast():
    async def go():
        port_a, port_b = _free_port(), _free_port()
        got_b = []
        async def on_a(kind, data, peer):
            pass
        async def on_b(kind, data, peer):
            got_b.append((kind, data, peer))
        mgr_a = LinkManager(_cfg("A", port_a), "node-aaaa", on_a)
        mgr_b = LinkManager(_cfg("B", port_b), "node-bbbb", on_b)
        tasks = [
            asyncio.create_task(mgr_a.serve()),
            asyncio.create_task(mgr_b.serve()),
            asyncio.create_task(mgr_a.apply_loop()),
            asyncio.create_task(mgr_b.apply_loop()),
        ]
        try:
            await asyncio.sleep(0.15)
            await mgr_a.ensure_link("127.0.0.1", port_b)
            for _ in range(60):
                if mgr_a.active_count() and mgr_b.active_count():
                    break
                await asyncio.sleep(0.05)
            assert mgr_a.active_count() == 1
            assert mgr_b.active_count() == 1
            await mgr_a.broadcast_clip("text", "mesh-hello")
            for _ in range(60):
                if got_b:
                    break
                await asyncio.sleep(0.05)
            assert ("text", "mesh-hello", "A") in got_b
        finally:
            for t in tasks:
                t.cancel()
            await asyncio.gather(*tasks, return_exceptions=True)
            await mgr_a.close()
            await mgr_b.close()
    asyncio.run(go())


# ---- global mDNS escalator keyed on active-link count ------------------

def test_idle_link_watchdog_fires_only_at_zero_active_links():
    async def go():
        import types as _types

        class FakeBeacon:
            def __init__(self):
                self.refreshes = 0
            async def refresh(self):
                self.refreshes += 1

        # One active link -> the global escalator must stay quiet.
        mgr = LinkManager(_cfg(), "node-self", None)
        mgr._links = {"a": FakeLink(name="a")}
        beacon = FakeBeacon()
        task = asyncio.create_task(
            anyclip.idle_link_watchdog(beacon, mgr, idle_threshold=0.02,
                                       refresh_attempts_before_bounce=3)
        )
        await asyncio.sleep(0.1)  # several idle_threshold ticks
        task.cancel()
        try:
            await task
        except asyncio.CancelledError:
            pass
        assert beacon.refreshes == 0  # links active -> no refresh

        # Zero active links -> escalation refreshes, then bounces.
        mgr2 = LinkManager(_cfg(), "node-self", None)  # empty table
        beacon2 = FakeBeacon()
        with_bounce = asyncio.create_task(
            anyclip.idle_link_watchdog(beacon2, mgr2, idle_threshold=0.02,
                                       refresh_attempts_before_bounce=2)
        )
        raised = False
        try:
            await asyncio.wait_for(with_bounce, timeout=1.0)
        except RuntimeError:
            raised = True
        except asyncio.TimeoutError:
            with_bounce.cancel()
        assert beacon2.refreshes >= 1
        assert raised  # escalated to a daemon bounce after the refresh budget
    asyncio.run(go())
