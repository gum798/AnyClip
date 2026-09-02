"""EchoSuppressor: bounded (30 s) echo suppression per kind.
Keep in lockstep with Swift EchoSuppressorTests and C# EchoSuppressorTests."""
from __future__ import annotations

from anyclip import ECHO_SUPPRESS_WINDOW_S, EchoSuppressor


def test_sends_when_nothing_received():
    s = EchoSuppressor()
    assert s.should_send("text", "h1", now=0.0)


def test_suppresses_echo_within_window():
    s = EchoSuppressor()
    s.mark_received("text", "h1", now=0.0)
    assert not s.should_send("text", "h1", now=0.0)
    assert not s.should_send("text", "h1", now=ECHO_SUPPRESS_WINDOW_S)
    assert s.should_send("text", "h2", now=0.0)


def test_deliberate_recopy_sends_after_window():
    # The 2026-09-02 password bug: the exact string last received from the
    # peer could never be re-sent, however much later the user re-copied it.
    s = EchoSuppressor()
    s.mark_received("text", "h1", now=0.0)
    assert s.should_send("text", "h1", now=ECHO_SUPPRESS_WINDOW_S + 0.001)
    assert s.should_send("text", "h1", now=87.0)


def test_remark_rearms_window():
    s = EchoSuppressor()
    s.mark_received("text", "h1", now=0.0)
    s.mark_received("text", "h1", now=40.0)
    assert not s.should_send("text", "h1", now=60.0)
    assert s.should_send("text", "h1", now=40.0 + ECHO_SUPPRESS_WINDOW_S + 0.001)


def test_suppressed_check_does_not_extend_window():
    s = EchoSuppressor()
    s.mark_received("text", "h1", now=0.0)
    assert not s.should_send("text", "h1", now=29.0)
    assert s.should_send("text", "h1", now=31.0)


def test_kinds_tracked_independently():
    s = EchoSuppressor()
    s.mark_received("text", "h1", now=0.0)
    assert s.should_send("image", "h1", now=0.0)
    assert not s.should_send("text", "h1", now=0.0)


def test_default_clock_suppresses_fresh_receive():
    # No explicit now: the real monotonic clock applies; a receive marked an
    # instant ago must still be suppressed.
    s = EchoSuppressor()
    s.mark_received("text", "h1")
    assert not s.should_send("text", "h1")
