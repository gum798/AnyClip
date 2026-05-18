"""Tests for permission_probe.

Three result branches are exercised with a fake clock (instant sleep)
and a fake event-feed counter, plus a sanity sweep of `decide()` and
one async case where mDNS traffic arrives mid-window.
"""

from __future__ import annotations

import asyncio
from typing import List

import pytest

from permission_probe import Result, decide, probe


def test_decide_table() -> None:
    assert decide(0, True) == Result.BLOCKED_LOCAL_NETWORK
    assert decide(5, True) == Result.OK
    assert decide(0, False) == Result.NO_NETWORK
    assert decide(5, False) == Result.NO_NETWORK  # no_network dominates
    assert decide(-1, True) == Result.BLOCKED_LOCAL_NETWORK


def _instant_sleep() -> "callable":
    async def sleep(_seconds: float) -> None:
        return None
    return sleep


@pytest.mark.asyncio
async def test_probe_blocked_local_network_when_no_events() -> None:
    result = await probe(
        events_seen_fn=lambda: 0,
        has_network_fn=lambda: True,
        wait_seconds=30.0,
        sleep_fn=_instant_sleep(),
    )
    assert result == Result.BLOCKED_LOCAL_NETWORK


@pytest.mark.asyncio
async def test_probe_ok_when_events_seen() -> None:
    result = await probe(
        events_seen_fn=lambda: 3,
        has_network_fn=lambda: True,
        wait_seconds=30.0,
        sleep_fn=_instant_sleep(),
    )
    assert result == Result.OK


@pytest.mark.asyncio
async def test_probe_no_network_dominates() -> None:
    result = await probe(
        events_seen_fn=lambda: 100,
        has_network_fn=lambda: False,
        wait_seconds=30.0,
        sleep_fn=_instant_sleep(),
    )
    assert result == Result.NO_NETWORK


@pytest.mark.asyncio
async def test_probe_reads_counter_after_sleep_not_before() -> None:
    """Events arriving *during* the window must count.

    Probe must call events_seen_fn() *after* sleep_fn returns, not at
    setup time -- otherwise late-arriving mDNS traffic is invisible to
    the judgment.
    """
    counter = [0]

    async def sleep_that_emits(_seconds: float) -> None:
        # Simulate mDNS event landing while probe is waiting.
        counter[0] += 1

    result = await probe(
        events_seen_fn=lambda: counter[0],
        has_network_fn=lambda: True,
        wait_seconds=30.0,
        sleep_fn=sleep_that_emits,
    )
    assert result == Result.OK


@pytest.mark.asyncio
async def test_probe_passes_wait_seconds_to_sleep() -> None:
    captured: List[float] = []

    async def sleep_capture(seconds: float) -> None:
        captured.append(seconds)

    await probe(
        events_seen_fn=lambda: 1,
        has_network_fn=lambda: True,
        wait_seconds=7.5,
        sleep_fn=sleep_capture,
    )
    assert captured == [7.5]
