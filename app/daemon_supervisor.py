"""Run the existing anyclip.py daemon inside a GUI process.

The GUI shell (rumps/pystray) needs the daemon as a background task,
not a subprocess: a subprocess would mean two PID files, two log
streams, no shared in-process event queue. So we spin up a dedicated
thread that owns its own asyncio loop, runs `anyclip.run(config)` on
it, and drains the `_event_bus` through `peer_state.reduce` into a
thread-safe `queue.Queue[State]` the GUI tail-reads on its own
display timer.
"""

from __future__ import annotations

import asyncio
import logging
import threading
import time
from queue import Queue
from typing import Optional

import anyclip
import peer_state

log = logging.getLogger("anyclip.daemon_supervisor")


class DaemonSupervisor:
    """Owns the daemon thread + bridges DaemonEvents to a UI-side Queue."""

    def __init__(self, config: anyclip.Config) -> None:
        self.config = config
        # Bounded so a paused UI cannot grow this without limit.
        self.state_queue: "Queue[peer_state.State]" = Queue(maxsize=1024)
        self._thread: Optional[threading.Thread] = None
        self._loop: Optional[asyncio.AbstractEventLoop] = None
        self._daemon_task: Optional[asyncio.Task] = None

    # ---- lifecycle -------------------------------------------------------

    def start(self) -> None:
        if self._thread is not None:
            return
        self._thread = threading.Thread(
            target=self._thread_main,
            name="anyclip-daemon",
            daemon=True,
        )
        self._thread.start()

    def stop(self, timeout: float = 5.0) -> None:
        loop = self._loop
        if loop is None:
            return
        loop.call_soon_threadsafe(self._cancel_all)
        t = self._thread
        if t is not None and t.is_alive():
            t.join(timeout=timeout)

    # ---- thread body -----------------------------------------------------

    def _thread_main(self) -> None:
        loop = asyncio.new_event_loop()
        self._loop = loop
        asyncio.set_event_loop(loop)
        try:
            loop.run_until_complete(self._co_main())
        except Exception:
            log.exception("daemon thread crashed")
        finally:
            try:
                loop.close()
            except Exception:
                pass

    async def _co_main(self) -> None:
        self._daemon_task = asyncio.create_task(
            anyclip.run(self.config), name="anyclip-run"
        )
        reducer_task = asyncio.create_task(
            self._reduce_events(), name="anyclip-reducer"
        )
        done, pending = await asyncio.wait(
            {self._daemon_task, reducer_task},
            return_when=asyncio.FIRST_COMPLETED,
        )
        for t in pending:
            t.cancel()
        for t in pending:
            try:
                await t
            except (asyncio.CancelledError, Exception):
                pass

    def _cancel_all(self) -> None:
        task = self._daemon_task
        if task is not None and not task.done():
            task.cancel()

    # ---- event drain -----------------------------------------------------

    async def _reduce_events(self) -> None:
        """Fold DaemonEvents through peer_state.reduce and publish."""
        state = peer_state.INITIAL
        self._publish(state)
        # The bus is created inside anyclip.run() after the loop starts.
        # Poll briefly until it appears so we do not race the daemon
        # startup and miss the first events.
        for _ in range(200):
            if anyclip._event_bus is not None:
                break
            await asyncio.sleep(0.05)
        bus = anyclip._event_bus
        if bus is None:
            log.warning("event bus never initialised; UI will stay idle")
            return
        while True:
            event = await bus.get()
            state = peer_state.reduce(state, event, now=time.monotonic())
            self._publish(state)

    def _publish(self, state: peer_state.State) -> None:
        try:
            self.state_queue.put_nowait(state)
        except Exception:
            # Queue full: drop. The UI only needs the latest state, so
            # losing intermediates is fine -- it will see the next one.
            pass
