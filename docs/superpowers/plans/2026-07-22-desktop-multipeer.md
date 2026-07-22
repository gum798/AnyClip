# Desktop Multi-Peer (1:N Full Mesh) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Every AnyClip desktop daemon links to every discovered same-token peer simultaneously (full mesh, capped), local clips broadcast to all active links, received clips apply locally with no relay — in all three implementations (Python, Swift, C#).

**Architecture:** Split each implementation's single-link `PeerLink` into a `LinkManager` (listening socket, active-link table keyed by `node_id`, pre-routing gate, broadcast, cap/re-admission) plus a narrowed per-peer `PeerLink` (post-hello session only). Event model gains peer identity; UI state becomes a peer collection; the mDNS idle escalator re-keys on zero active links. Spec: `docs/superpowers/specs/2026-07-22-desktop-multipeer-design.md`.

**Tech Stack:** Python 3.12 asyncio (`anyclip.py`, `peer_state.py`, `app/`), Swift 6 actors (`formacOS/`), C# .NET 8 (`forwindows/`). Test commands per CLAUDE.md.

## Global Constraints

- **No wire change.** Protocol stays 1.1; hello, framing, golden vectors, interop fixtures byte-identical. App version 1.3.0 comes from the release tag — no version constants change.
- **Precondition:** the v1.2.1 zombie-daemon hotfix (`FramedConnection.start()` cancellation + `.waiting` fail-fast, `formacOS`) must be merged before Task 5 — the Swift LinkManager builds on `tryConnect`/`start()` and Task 5's shutdown/cancellation tests deadlock without it.
- **Cross-implementation contract** (identical semantics, per-language naming):
  - Events: `LinkUp`/`LinkDown` carry `node_id` + peer name (Python `LinkUp(node_id, peer_name, app_version, protocol_minor)` / `LinkDown(node_id, reason)`; Swift `.linkUp(nodeID:peerName:)` / `.linkDown(nodeID:reason:)`; C# `LinkUp(string NodeId, string PeerName)` / `LinkDown(string NodeId, string Reason)`).
  - UI state: peers dict `node_id → display name`; 0 peers → existing "searching" presentation; ≥1 → `"Linked: " + names sorted ordinally, comma-joined`. Reducer inserts/updates on LinkUp, removes only that `node_id` on LinkDown; linked iff non-empty.
  - Cap: `DEFAULT_MAX_PEERS = 8` / `defaultMaxPeers = 8` / `DefaultMaxPeers = 8`; Python-only `--max-peers` CLI flag; `~/.anyclip/config.json` stays token-only. Cap check AFTER `node_id` routing — known-peer reconnects always routed; only NEW `node_id`s refused at cap, log `peer cap reached (N); refusing <name>`.
  - Duplicate connection for a live `node_id` REPLACES the session, log `replacing link with <name> (peer reconnected)`; lexicographic-`node_id` tie-break only inside the handshake window. Dead link → table entry removed immediately (frees cap slot); per-address discovery backoff unchanged.
  - Broadcast fans out to all active links; per-link send failure drops only that link; per-link protocol-minor gating (`"files"` vs first-file fallback) evaluated per link. Receive applies serialized through one queue; each apply marks the global suppressor before touching the clipboard.
  - Watchdogs: per-link staleness dropper; global mDNS escalator fires ONLY at zero active links.
  - Skip/fallback toasts for one local copy aggregated into ONE notification across all peers.
- **Execution order:** Tasks 1–3 (Python) → 4–6 (Swift) → 7–9 (C#) → 10 (docs). Within each part the order is hard (event model → LinkManager → shells/interop); the three parts share no code, but run them in this order anyway — Python is the source-of-truth reference when a semantics question comes up.
- Run the full test suite of every implementation you touch before each commit; interop and golden tests must stay green throughout (wire unchanged is an asserted property, not an assumption).

---

# Part 1 — Python (Tasks 1–3)


**Goal:** Turn the Python (legacy, source-of-truth) daemon from single-link to a full mesh: every daemon links to every discovered same-token peer (up to a cap) and local clips broadcast to all active links. Split the single-link `PeerLink` into a `LinkManager` (listening socket + active-link table keyed by `node_id` + pre-routing gate + broadcast) and a narrowed per-peer `PeerLink` (post-hello session only). Spec: `docs/superpowers/specs/2026-07-22-desktop-multipeer-design.md`.

**No wire change.** Protocol stays 1.1; hello/framing/golden vectors/interop fixtures untouched. Shipped as app version 1.3.0 (comes from the release tag — no version constants change).

**Tech stack:** Python 3.12 asyncio, repo root `anyclip.py` + `peer_state.py` + `app/` GUI shells. Build/test per `CLAUDE.md`: `source .venv/bin/activate && pytest tests/ -v`.

## Global constraints (apply to every task)

- **Event model (fixed cross-impl contract):** `LinkUp(node_id: str, peer_name: str, app_version: str, protocol_minor: int)`; `LinkDown(node_id: str, reason: str)`.
- **UI state:** `peer_state.State.peers: Mapping[str, str]` maps `node_id -> display name`. Zero peers → existing "searching" presentation unchanged; ≥1 → status text `"Linked: " + names sorted ordinally, comma-joined`. Reducer: `LinkUp` inserts/updates by `node_id`; `LinkDown` removes ONLY that `node_id`; state is `"linked"` iff `peers` is non-empty.
- **LinkManager:** owns listening socket, active-link table keyed by `node_id`, the pre-routing gate (AuthGate ip-block+record, token check, version-negotiation major-mismatch refusal, self/loopback drop) run AFTER the hello exchange and BEFORE routing. Cap constant `DEFAULT_MAX_PEERS = 8`; Python-only `--max-peers` CLI flag; `config.json` stays token-only. Cap check runs AFTER `node_id` routing (a known `node_id` reconnect is always routed, never refused); refuses only NEW `node_id`s when at cap. Duplicate connection for a `node_id` with a live session replaces the old session (close old socket). Tie-break (lexicographic `node_id`) applies only when two connections for the same `node_id` are both inside the handshake window. Dead link → entry removed from table immediately. Broadcast fans out to all active links; a per-link send failure drops only that link; per-link protocol-minor gating (files vs first-file fallback) evaluated per link. Receive applies serialized through ONE queue; each apply marks the global `EchoSuppressor` before touching the clipboard.
- **Watchdogs:** per-link staleness dropper (`link_ping_loop`→`drop_stale_link`) runs one instance per link; global mDNS-health escalator (`idle_link_watchdog`) fires ONLY when zero links are active (keys on the manager's active-link count).
- **Commits** end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`. Work on branch `feat/desktop-multipeer`.
- **Task order is a hard dependency chain:** Task 1 → Task 2 → Task 3. Task 2 depends on Task 1's `LinkUp`/`LinkDown` shapes; Task 3 depends on Task 2's `LinkManager` (watchdog re-keying, `run()` coros) and on Task 1's `peer_state.status_label`.
- **Intermediate-state note:** after Task 2, `run()` is validated only by unit tests (`tests/test_link_manager.py`) that construct `LinkManager` directly — `run()`'s coroutine list still references the old single `link` binding and is finished in Task 3. `pytest tests/` is green at every task boundary because no test executes `anyclip.run()`.

---

### Task 1: `peer_state.py` — multi-peer event model, reducer, and shared status label

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/peer_state.py`
  - imports (lines 15-16): add `field` (dataclasses) and `Mapping` (typing)
  - `LinkUp` (lines 28-32) and `LinkDown` (lines 34-37): new fields
  - `State` (lines 62-76): replace scalar `peer_name` with `peers` dict
  - `reduce` LinkUp/LinkDown branches (lines 96-109)
  - append `linked_names` + `status_label` helpers at end of file
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_peer_state.py` (update existing tests, append new ones)

**Interfaces:**
- Consumes: `dataclasses.dataclass/field/replace`, `typing.Mapping/Optional/Union` (stdlib).
- Produces:
  - `LinkUp(node_id: str, peer_name: str, app_version: str, protocol_minor: int)` (frozen dataclass)
  - `LinkDown(node_id: str, reason: str)` (frozen dataclass)
  - `State(kind: str, peers: Mapping[str, str] = {}, since: Optional[float] = None, reason: Optional[str] = None, consecutive_handshake_fails: int = 0)` (frozen dataclass; `peers` via `field(default_factory=dict)`)
  - `reduce(prev: State, event: DaemonEvent, now: float) -> State` (unchanged signature; LinkUp/LinkDown semantics changed)
  - `linked_names(state: State) -> list` (peer display names, sorted ordinally)
  - `status_label(state: State) -> str` (single-line status text shared by both GUI shells)

- [ ] **Step 1: Update the failing tests** — Edit `tests/test_peer_state.py`. There are five existing tests that construct `LinkUp`/`LinkDown` or assert single-peer state; update each, then append the multi-peer + helper tests.

  Edit 1 — `test_discovered_then_linkup` (lines 39-47). Replace:
  ```python
  def test_discovered_then_linkup() -> None:
      final = _fold([
          PeerDiscovered(name="peer-1", addr="10.0.0.2:24816"),
          LinkUp(peer_name="peer-1", peer_id="abcd1234"),
      ], now=42.0)
      assert final.kind == "linked"
      assert final.peer_name == "peer-1"
      assert final.since == 42.0
  ```
  with:
  ```python
  def test_discovered_then_linkup() -> None:
      final = _fold([
          PeerDiscovered(name="peer-1", addr="10.0.0.2:24816"),
          LinkUp(node_id="abcd1234", peer_name="peer-1",
                 app_version="1.3.0", protocol_minor=1),
      ], now=42.0)
      assert final.kind == "linked"
      assert final.peers == {"abcd1234": "peer-1"}
      assert final.since == 42.0
  ```

  Edit 2 — `test_linkup_then_linkdown_goes_to_searching` (lines 49-55). Replace:
  ```python
  def test_linkup_then_linkdown_goes_to_searching() -> None:
      final = _fold([
          LinkUp(peer_name="peer-1", peer_id="abcd1234"),
          LinkDown(reason="peer disconnected"),
      ])
      assert final.kind == "searching"
      assert final.reason == "peer disconnected"
  ```
  with:
  ```python
  def test_linkup_then_linkdown_goes_to_searching() -> None:
      final = _fold([
          LinkUp(node_id="abcd1234", peer_name="peer-1",
                 app_version="1.3.0", protocol_minor=1),
          LinkDown(node_id="abcd1234", reason="peer disconnected"),
      ])
      assert final.kind == "searching"
      assert final.reason == "peer disconnected"
  ```

  Edit 3 — `test_linkup_resets_handshake_failure_counter` (lines 72-82). Replace the two `LinkUp(peer_name="p", peer_id="i")` calls:
  ```python
      state = reduce(state, LinkUp(peer_name="p", peer_id="i"), now=2.0)
  ```
  with:
  ```python
      state = reduce(state, LinkUp(node_id="i", peer_name="p",
                                   app_version="1.3.0", protocol_minor=1), now=2.0)
  ```

  Edit 4 — `test_discovered_while_linked_is_noop` (lines 96-99). Replace:
  ```python
      state = reduce(INITIAL, LinkUp(peer_name="p", peer_id="i"), now=1.0)
  ```
  with:
  ```python
      state = reduce(INITIAL, LinkUp(node_id="i", peer_name="p",
                                     app_version="1.3.0", protocol_minor=1), now=1.0)
  ```

  Edit 5 — `test_reducer_is_pure_for_identical_sequence` (lines 109-119). Replace:
  ```python
      seq = [
          PeerDiscovered(name="p", addr="x"),
          LinkUp(peer_name="p", peer_id="i"),
          LinkDown(reason="bye"),
          PeerDiscovered(name="p", addr="x"),
          LinkUp(peer_name="p", peer_id="i"),
      ]
  ```
  with:
  ```python
      seq = [
          PeerDiscovered(name="p", addr="x"),
          LinkUp(node_id="i", peer_name="p", app_version="1.3.0", protocol_minor=1),
          LinkDown(node_id="i", reason="bye"),
          PeerDiscovered(name="p", addr="x"),
          LinkUp(node_id="i", peer_name="p", app_version="1.3.0", protocol_minor=1),
      ]
  ```

  Edit 6 — append the new multi-peer + helper tests. Anchor on the end of the file:
  ```python
  @pytest.mark.parametrize("threshold", [HANDSHAKE_FAIL_THRESHOLD])
  def test_threshold_constant_is_five(threshold: int) -> None:
      """Slice AC pins the threshold to 5 handshake failures."""
      assert threshold == 5
  ```
  Replace with:
  ```python
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
  ```

  Add the two helper names to the import block at the top of the file (lines 14-25). Replace:
  ```python
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
      reduce,
  )
  ```
  with:
  ```python
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
  ```

- [ ] **Step 2: Run tests to verify they fail** — `source .venv/bin/activate && pytest tests/test_peer_state.py -v`
  Expected: import error `ImportError: cannot import name 'linked_names' from 'peer_state'` (helpers not defined yet); once past import, the updated `LinkUp(...)`/`LinkDown(...)` constructions raise `TypeError: __init__() got an unexpected keyword argument 'node_id'` / `'app_version'` because the dataclasses still have the old fields.

- [ ] **Step 3a: Update imports** — Edit `peer_state.py` lines 15-16. Replace:
  ```python
  from dataclasses import dataclass, replace
  from typing import Optional, Union
  ```
  with:
  ```python
  from dataclasses import dataclass, field, replace
  from typing import Mapping, Optional, Union
  ```

- [ ] **Step 3b: New event shapes** — Edit `peer_state.py`. Anchor on lines 28-37:
  ```python
  @dataclass(frozen=True)
  class LinkUp:
      peer_name: str
      peer_id: str


  @dataclass(frozen=True)
  class LinkDown:
      reason: str
  ```
  Replace with:
  ```python
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
  ```

- [ ] **Step 3c: State becomes a peer collection** — Edit `peer_state.py`. Anchor on lines 62-76:
  ```python
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
  ```
  Replace with:
  ```python
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
  ```

- [ ] **Step 3d: Reducer LinkUp/LinkDown branches** — Edit `peer_state.py`. Anchor on lines 96-109:
  ```python
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
  ```
  Replace with:
  ```python
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
  ```

- [ ] **Step 3e: Append the display helpers** — Edit `peer_state.py`. Anchor on the tail of the reducer (lines 119-129):
  ```python
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
  ```
  Replace with:
  ```python
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
  ```

- [ ] **Step 4: Run tests to verify they pass** — `source .venv/bin/activate && pytest tests/test_peer_state.py -v`
  Expected: all pass (the 11 pre-existing tests, now updated, plus the 8 new multi-peer / helper tests).

- [ ] **Step 5: Commit**
  ```
  git add peer_state.py tests/test_peer_state.py
  git commit -m "$(cat <<'EOF'
  feat(state): multi-peer event model + peers-keyed reducer

  LinkUp/LinkDown gain a stable node_id (+ app_version/protocol_minor on
  LinkUp); State replaces the scalar peer_name with a peers dict keyed by
  node_id. LinkUp inserts/updates by node_id, LinkDown removes only that
  node_id, and linked iff peers is non-empty. Add shared linked_names /
  status_label helpers for the GUI shells.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

---

### Task 2: `anyclip.py` — split PeerLink into LinkManager + narrowed PeerLink

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - add `DEFAULT_MAX_PEERS = 8` constant after `MAX_RECONNECT_FAILS` (line 100)
  - replace the whole `class PeerLink` block (lines 1299-1806, up to `class MdnsBeacon:` at 1809) with module framing helpers + narrowed `PeerLink` + `LinkManager`
  - add `send_files_to_link` + refactor `emit_files_clip` (lines 2200-2218)
  - rewrite the `on_remote_clip` / `on_local_change` closures and the manager/beacon construction inside `run()` (lines 2236-2383); **do NOT touch the coroutine list (lines 2388-2404) or the `finally` (lines 2405-2421) — those are Task 3**
  - `Config` dataclass (lines 708-716): add `max_peers`
  - `parse_args` (lines 772-850): add `--max-peers` flag + pass it into `Config`
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_link_manager.py` (create)
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_receive_files.py` (update one existing test for the new `PeerLink.__init__`)

**Interfaces:**
- Consumes: `sha256_hex` (line 356), `sha256_bytes` (line 360), `AuthGate` (line 1257, `is_blocked`/`record_fail`/`record_ok`), `VersionInfo`/`negotiate`/`link_allowed`/`Compatibility` (lines 47-52), `emit_event` (line 127), `HandshakeFailed`/`LinkUp`/`LinkDown` (Task 1 shapes), `decode_files_payload` / `aggregate_files_hash` (existing wire helpers), `link_ping_loop(link, ...)` (unchanged, line 2048), constants `SEND_TIMEOUT`/`HANDSHAKE_TIMEOUT`/`CONNECT_TIMEOUT`/`RACE_WINDOW_S`/`MAX_PAYLOAD`/`PROTOCOL_*`/`APP_VERSION`, `FatalStartupError`, `_enable_keepalive`/`_safe_close` (produced here).
- Produces:
  - module constant `DEFAULT_MAX_PEERS = 8`
  - `_safe_close(writer) -> None`, `_enable_keepalive(writer) -> None` (module-level, moved off PeerLink)
  - `_write_frame(writer, obj: dict, timeout: float) -> bool`, `_read_frame(reader) -> Optional[dict]` (handshake framing)
  - narrowed `class PeerLink` with `PeerLink(node_id, peer_node_id, peer_name, peer_protocol_minor, reader, writer, on_clip, remote_addr=None, send_timeout=SEND_TIMEOUT)`; attrs/props `active`, `linked_at`, `peer_name`, `peer_node_id`, `peer_protocol_minor`, `remote_addr`; methods `run_recv()`, `send_clip(kind, content)`, `send_ping()`, `seconds_since_inbound()`, `drop_stale_link(idle_seconds)`, `close()`
  - `class LinkManager` with `LinkManager(config, node_id, on_clip, max_peers=DEFAULT_MAX_PEERS)` and: `active_count()`, `has_link_to_addr(host, port)`, `peer_names()`, `attach_beacon(beacon)`, `apply_loop()`, `serve()`, `ensure_link(host, port)`, `broadcast_clip(kind, content)`, `broadcast_files(data) -> tuple[int, int, int]`, `redial_discovered(beacon)`, `close()` (plus internal `_route`, `_serve_link`, `_handshake_and_route`, `_handle_inbound`, `_enqueue_received`, `_keep_new`, `_drop_link`)
  - `send_files_to_link(link, data) -> tuple` returning `("files", n)` | `("file", dropped)`
  - `emit_files_clip(link, suppressor, data) -> tuple` (unchanged returns: `("suppressed", 0)` | `("files", n)` | `("file", dropped)`; now delegates to `send_files_to_link`)
  - `Config.max_peers: int` (default `DEFAULT_MAX_PEERS`); `--max-peers` CLI flag
  - `run()` closure `on_remote_clip(kind, data, peer_name)`; `on_local_change` broadcasts through the manager

- [ ] **Step 1: Write the failing tests** — create `tests/test_link_manager.py`:
  ```python
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
  ```

- [ ] **Step 2: Update the one existing PeerLink-constructor test** — Edit `tests/test_receive_files.py`. The `PeerLink.__init__` signature changes; `test_peer_protocol_minor_initialized_by_init` (lines 27-30) constructs it the old way. Anchor on:
  ```python
  def test_peer_protocol_minor_initialized_by_init():
      cfg = types.SimpleNamespace(token="tok")
      link = anyclip.PeerLink(cfg, "node-1", None)
      assert link.peer_protocol_minor is None
  ```
  Replace with:
  ```python
  def test_peer_protocol_minor_initialized_by_init():
      # Narrowed PeerLink now receives the parsed hello identity directly.
      link = anyclip.PeerLink(
          "node-self", "peer-node", "peer", 1,
          reader=None, writer=None, on_clip=None,
      )
      assert link.peer_protocol_minor == 1
      assert link.peer_name == "peer"
      assert link.remote_addr is None
  ```

- [ ] **Step 3: Run tests to verify they fail** — `source .venv/bin/activate && pytest tests/test_link_manager.py tests/test_receive_files.py -v`
  Expected: `test_link_manager.py` fails at collection with `ImportError: cannot import name 'LinkManager' from 'anyclip'`; the updated `test_peer_protocol_minor_initialized_by_init` fails `TypeError` (old `PeerLink.__init__` signature).

- [ ] **Step 4a: Add the peer-cap constant** — Edit `anyclip.py`. Anchor on lines 96-100:
  ```python
  # After this many consecutive failed outbound attempts to the same
  # (host, port) the address is pruned from known_peers. mDNS rediscovery
  # re-adds it automatically. Keeps the daemon from poking forever at a
  # stale IP after the peer DHCP-renewed onto a different address.
  MAX_RECONNECT_FAILS = 3
  ```
  Replace with:
  ```python
  # After this many consecutive failed outbound attempts to the same
  # (host, port) the address is pruned from known_peers. mDNS rediscovery
  # re-adds it automatically. Keeps the daemon from poking forever at a
  # stale IP after the peer DHCP-renewed onto a different address.
  MAX_RECONNECT_FAILS = 3
  # Full-mesh cap: at most this many simultaneous active links. Shared
  # constant across all three implementations; overridable in the Python
  # build only via --max-peers (config.json stays token-only).
  DEFAULT_MAX_PEERS = 8
  ```

- [ ] **Step 4b: Add `max_peers` to `Config`** — Edit `anyclip.py`. Anchor on lines 708-716:
  ```python
  @dataclass
  class Config:
      token: str
      port: int
      name: str
      poll_interval: float
      verbose: bool
      peers: list  # list[tuple[str, int]]; manual fallback peers
      no_notify: bool
  ```
  Replace with:
  ```python
  @dataclass
  class Config:
      token: str
      port: int
      name: str
      poll_interval: float
      verbose: bool
      peers: list  # list[tuple[str, int]]; manual fallback peers
      no_notify: bool
      max_peers: int = DEFAULT_MAX_PEERS  # mesh cap; --max-peers overrides
  ```

- [ ] **Step 4c: Add the `--max-peers` flag** — Edit `anyclip.py`. Anchor on lines 770-778:
  ```python
      parser.add_argument("--no-notify", action="store_true",
                          help="Suppress desktop toast notifications on clipboard sync")
      parser.add_argument(
          "--headless",
          action="store_true",
          help="Skip the menubar/tray GUI and run as a plain daemon. "
               "Default when the GUI dependencies (rumps/pystray) are "
               "unavailable.",
      )
  ```
  Replace with:
  ```python
      parser.add_argument("--no-notify", action="store_true",
                          help="Suppress desktop toast notifications on clipboard sync")
      parser.add_argument(
          "--max-peers", type=int, default=DEFAULT_MAX_PEERS,
          help="Maximum simultaneous mesh links (default: "
               f"{DEFAULT_MAX_PEERS}). New peers beyond the cap are refused; "
               "known peers reconnecting are always admitted.",
      )
      parser.add_argument(
          "--headless",
          action="store_true",
          help="Skip the menubar/tray GUI and run as a plain daemon. "
               "Default when the GUI dependencies (rumps/pystray) are "
               "unavailable.",
      )
  ```

- [ ] **Step 4d: Pass `max_peers` into the returned `Config`** — Edit `anyclip.py`. Anchor on lines 842-850:
  ```python
      return Config(
          token=token,
          port=args.port,
          name=args.name,
          poll_interval=max(0.1, args.poll),
          verbose=args.verbose,
          peers=list(args.peer or []),
          no_notify=args.no_notify,
      )
  ```
  Replace with:
  ```python
      return Config(
          token=token,
          port=args.port,
          name=args.name,
          poll_interval=max(0.1, args.poll),
          verbose=args.verbose,
          peers=list(args.peer or []),
          no_notify=args.no_notify,
          max_peers=max(1, args.max_peers),
      )
  ```

- [ ] **Step 4e: Replace the whole `class PeerLink` block with framing helpers + narrowed PeerLink + LinkManager** — Edit `anyclip.py`. This replaces lines 1299-1807 (the entire `class PeerLink:` through the blank line before `class MdnsBeacon:`). Anchor on the class header and its final method:
  ```python
  class PeerLink:
      """Owns the single active TCP link to a peer.

      Acts as both server and client; resolves the simultaneous-connect
      race via lexicographic node_id tie-break.
      """
  ```
  ...through the tail of `send_clip` (the very end of the class):
  ```python
              log.debug(f"send_clip: unknown kind {kind!r}, dropping")
              return
          await self._send(writer, payload)
  ```
  Replace the ENTIRE span (`class PeerLink:` … `await self._send(writer, payload)`) with:
  ```python
  def _safe_close(writer: asyncio.StreamWriter) -> None:
      try:
          writer.close()
      except Exception:
          pass


  def _enable_keepalive(writer: asyncio.StreamWriter) -> None:
      """Turn on TCP keepalive so the OS reaps silent zombies in ~30s.

      Windows ignores the per-socket tuning (its KeepAliveTime is a registry
      value defaulting to ~2h) -- the stale-link replacement handles that gap.
      On macOS the KEEPIDLE symbolic constant is absent from Python stdlib, so
      we use the raw value 0x10.
      """
      sock = writer.get_extra_info("socket")
      if sock is None:
          return
      try:
          sock.setsockopt(socket.SOL_SOCKET, socket.SO_KEEPALIVE, 1)
          if sys.platform == "linux":
              sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPIDLE, 15)
              sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPINTVL, 5)
              sock.setsockopt(socket.IPPROTO_TCP, socket.TCP_KEEPCNT, 3)
          elif sys.platform == "darwin":
              sock.setsockopt(socket.IPPROTO_TCP, 0x10, 15)  # TCP_KEEPALIVE
      except OSError as exc:
          log.debug(f"keepalive setup failed: {exc}")


  async def _write_frame(writer: asyncio.StreamWriter, obj: dict, timeout: float) -> bool:
      """Length-prefixed JSON frame write for the LinkManager handshake.
      Returns True on success; closes the writer and returns False on a wedged
      drain or over-cap payload (mirrors PeerLink._send)."""
      data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
      if len(data) > MAX_PAYLOAD:
          log.warning(f"payload too large ({len(data)} bytes), dropping")
          return False
      try:
          writer.write(len(data).to_bytes(4, "big"))
          writer.write(data)
          await asyncio.wait_for(writer.drain(), timeout=timeout)
          return True
      except asyncio.TimeoutError:
          log.info("handshake send timed out; dropping connection")
          _safe_close(writer)
          return False
      except Exception as exc:
          log.info(f"handshake send failed: {exc}")
          return False


  async def _read_frame(reader: asyncio.StreamReader) -> Optional[dict]:
      """Length-prefixed JSON frame read for the LinkManager handshake."""
      try:
          head = await reader.readexactly(4)
      except asyncio.IncompleteReadError:
          return None
      n = int.from_bytes(head, "big")
      if n == 0 or n > MAX_PAYLOAD:
          log.warning(f"invalid frame length: {n}")
          return None
      try:
          body = await reader.readexactly(n)
      except asyncio.IncompleteReadError:
          return None
      try:
          return json.loads(body.decode("utf-8"))
      except Exception as exc:
          log.warning(f"bad json: {exc}")
          return None


  class PeerLink:
      """One authenticated peer session (exactly one peer pair).

      Created by LinkManager AFTER the hello exchange and gate; it never reads
      a hello itself -- it receives the parsed identity (peer node_id, name,
      protocol minor, app version) and the already-open reader/writer. Owns the
      receive loop, per-link send, app-layer keepalive, and the per-link
      staleness watchdog. The listening socket, active-link table, gate, and
      broadcast live in LinkManager.
      """

      def __init__(
          self,
          node_id: str,
          peer_node_id: str,
          peer_name: str,
          peer_protocol_minor: int,
          reader: Optional[asyncio.StreamReader],
          writer: Optional[asyncio.StreamWriter],
          on_clip,
          remote_addr: Optional[tuple] = None,
          send_timeout: float = SEND_TIMEOUT,
      ) -> None:
          self.node_id = node_id            # our own node_id
          self.peer_node_id = peer_node_id  # this peer's node_id (table key)
          self.peer_name = peer_name
          self.peer_protocol_minor = peer_protocol_minor
          self._reader = reader
          self._writer = writer
          # async (kind, data, peer_name) -> None; LinkManager's serialized
          # apply-queue enqueue. Never applies to the clipboard directly.
          self._on_clip = on_clip
          # (host, port) for OUTBOUND links (so re-admission can skip an
          # address already backed by a live link); None for inbound.
          self.remote_addr = remote_addr
          self._send_timeout = send_timeout
          self._linked_at = time.monotonic()
          self._last_inbound = time.monotonic()

      @property
      def active(self) -> bool:
          return self._writer is not None and not self._writer.is_closing()

      @property
      def linked_at(self) -> float:
          return self._linked_at

      async def run_recv(self) -> None:
          """Drain frames until EOF/error, enqueuing received clips through the
          on_clip callback (LinkManager's serialized apply queue). Never reads a
          hello -- that already happened in the manager handshake. Closes the
          writer on exit so `active` flips False and the table entry is reaped.
          """
          reader, writer = self._reader, self._writer
          try:
              while True:
                  msg = await self._recv(reader)
                  if msg is None:
                      break
                  self._last_inbound = time.monotonic()
                  msg_type = msg.get("type")
                  if msg_type == "clip":
                      kind = msg.get("kind", "text")
                      content = msg.get("content")
                      if kind == "text" and isinstance(content, str):
                          await self._on_clip("text", content, self.peer_name)
                      elif kind == "image" and isinstance(content, str):
                          try:
                              png = base64.b64decode(content, validate=True)
                          except Exception as exc:
                              log.warning(f"bad image payload from peer: {exc}")
                              continue
                          await self._on_clip("image", png, self.peer_name)
                      elif kind == "file" and isinstance(content, str):
                          try:
                              raw = base64.b64decode(content, validate=True)
                          except Exception as exc:
                              log.warning(f"bad file payload from peer: {exc}")
                              continue
                          name = msg.get("name") or "received.bin"
                          if not isinstance(name, str):
                              name = "received.bin"
                          await self._on_clip("file", (name, raw), self.peer_name)
                      elif kind == "files":
                          decoded = decode_files_payload(msg)
                          if decoded is None:
                              continue  # whole-frame drop already logged
                          await self._on_clip("files", decoded, self.peer_name)
                      else:
                          log.debug(f"ignoring clip with kind={kind!r}")
                  elif msg_type == "ping":
                      await self._send(writer, {"type": "pong", "ts": time.time()})
                  elif msg_type == "pong":
                      pass
                  else:
                      log.debug(f"ignoring message type: {msg_type!r}")
          finally:
              _safe_close(writer)
              self._writer = None

      async def _send(self, writer: asyncio.StreamWriter, obj: dict) -> None:
          data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
          if len(data) > MAX_PAYLOAD:
              log.warning(f"payload too large ({len(data)} bytes), dropping")
              return
          try:
              writer.write(len(data).to_bytes(4, "big"))
              writer.write(data)
              await asyncio.wait_for(writer.drain(), timeout=self._send_timeout)
          except asyncio.TimeoutError:
              # The write parked past the budget -- half-open/wedged socket.
              # Close the writer so the next _recv returns EOF and the session
              # tears down; never let a stuck send freeze the caller's loop.
              log.info("send timed out (link wedged); dropping link to force reconnect")
              _safe_close(writer)
          except Exception as exc:
              log.info(f"send failed (link likely down): {exc}")

      async def _recv(self, reader: asyncio.StreamReader) -> Optional[dict]:
          try:
              head = await reader.readexactly(4)
          except asyncio.IncompleteReadError:
              return None
          n = int.from_bytes(head, "big")
          if n == 0 or n > MAX_PAYLOAD:
              log.warning(f"invalid frame length: {n}")
              return None
          try:
              body = await reader.readexactly(n)
          except asyncio.IncompleteReadError:
              return None
          try:
              return json.loads(body.decode("utf-8"))
          except Exception as exc:
              log.warning(f"bad json: {exc}")
              return None

      async def close(self) -> None:
          """Drop this link's socket. Safe to call multiple times."""
          if self._writer is not None:
              _safe_close(self._writer)
              self._writer = None

      async def send_ping(self) -> None:
          """App-layer keepalive frame. Drives traffic on an otherwise idle
          link so a silently-dead TCP socket surfaces as a send failure + EOF
          on the next recv -- important on Windows where the OS KeepAliveTime
          defaults to ~2h."""
          writer = self._writer
          if writer is None or writer.is_closing():
              return
          await self._send(writer, {"type": "ping", "ts": time.time()})

      def seconds_since_inbound(self) -> Optional[float]:
          """Seconds since the last inbound frame, or None if not linked. The
          per-link heartbeat compares this against its deadline."""
          if not self.active:
              return None
          return time.monotonic() - self._last_inbound

      def drop_stale_link(self, idle_seconds: float) -> None:
          """Drop a link gone silent -- half-open socket: the peer slept or
          vanished without RST/FIN, so sends never error and _recv never
          returns. Closing the writer makes the next _recv return None, the
          session tears down (freeing the table slot), and re-admission runs.
          No-op if already unlinked."""
          writer = self._writer
          if writer is None:
              return
          log.info(
              f"link to {self.peer_name!r} idle {int(idle_seconds)}s with no "
              f"inbound (peer likely asleep / half-open); dropping to force reconnect"
          )
          _safe_close(writer)

      async def send_clip(self, kind: str, content) -> None:
          """Send one clipboard payload to THIS peer. kind=='text' expects str,
          'image' raw PNG bytes, 'file' (name, raw), 'files' [(name, raw), ...]."""
          writer = self._writer
          if writer is None or writer.is_closing():
              return
          if kind == "text":
              if not isinstance(content, str):
                  return
              payload = {
                  "type": "clip",
                  "kind": "text",
                  "content": content,
                  "hash": sha256_hex(content),
                  "ts": time.time(),
              }
          elif kind == "image":
              if not isinstance(content, (bytes, bytearray)):
                  return
              encoded = base64.b64encode(bytes(content)).decode("ascii")
              payload = {
                  "type": "clip",
                  "kind": "image",
                  "content": encoded,
                  "hash": sha256_bytes(bytes(content)),
                  "ts": time.time(),
                  "bytes": len(content),
              }
          elif kind == "file":
              if not isinstance(content, tuple) or len(content) != 2:
                  return
              name, raw = content
              if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                  return
              raw_b = bytes(raw)
              encoded = base64.b64encode(raw_b).decode("ascii")
              payload = {
                  "type": "clip",
                  "kind": "file",
                  "name": name,
                  "content": encoded,
                  "hash": sha256_bytes(raw_b),
                  "ts": time.time(),
                  "bytes": len(raw_b),
              }
          elif kind == "files":
              if not isinstance(content, list) or not content:
                  return
              files_arr = []
              hashes = []
              total = 0
              for ent in content:
                  if not isinstance(ent, tuple) or len(ent) != 2:
                      return
                  name, raw = ent
                  if not isinstance(name, str) or not isinstance(raw, (bytes, bytearray)):
                      return
                  raw_b = bytes(raw)
                  h = sha256_bytes(raw_b)
                  files_arr.append({
                      "name": name,
                      "content": base64.b64encode(raw_b).decode("ascii"),
                      "hash": h,
                      "bytes": len(raw_b),
                  })
                  hashes.append(h)
                  total += len(raw_b)
              payload = {
                  "type": "clip",
                  "kind": "files",
                  "files": files_arr,
                  "hash": aggregate_files_hash(hashes),
                  "ts": time.time(),
                  "bytes": total,
              }
          else:
              log.debug(f"send_clip: unknown kind {kind!r}, dropping")
              return
          await self._send(writer, payload)


  class LinkManager:
      """Owns the listening socket, the active-link table keyed by peer
      node_id, the pre-routing gate, and clip broadcast. Splits the old
      single-link PeerLink into a router (this) + N per-peer PeerLink sessions.
      """

      def __init__(self, config: "Config", node_id: str, on_clip,
                   max_peers: int = DEFAULT_MAX_PEERS) -> None:
          self.config = config
          self.node_id = node_id
          # async (kind, data, peer_name) -> None; the REAL apply handler
          # (on_remote_clip), invoked serially by apply_loop.
          self._on_clip = on_clip
          self.max_peers = max_peers
          self._token_hash = sha256_hex(config.token)
          self._auth_gate = AuthGate()          # moved here: fails aggregate per IP
          self._links: dict = {}                # peer node_id -> PeerLink
          self._lock = asyncio.Lock()
          self._apply_queue: asyncio.Queue = asyncio.Queue()
          self._connecting: set = set()         # (host, port) dials in flight
          self._beacon = None                   # set via attach_beacon

      # ---- introspection -------------------------------------------------
      def active_count(self) -> int:
          return sum(1 for l in self._links.values() if l.active)

      def has_link_to_addr(self, host: str, port: int) -> bool:
          key = (host, port)
          return any(l.active and l.remote_addr == key for l in self._links.values())

      def peer_names(self) -> list:
          return [l.peer_name for l in self._links.values() if l.active]

      def attach_beacon(self, beacon) -> None:
          """Wire the discovery snapshot so link drops can actively re-dial."""
          self._beacon = beacon

      # ---- serialized receive apply --------------------------------------
      async def _enqueue_received(self, kind, data, peer_name) -> None:
          await self._apply_queue.put((kind, data, peer_name))

      async def apply_loop(self) -> None:
          """Single consumer across ALL links: applies received clips one at a
          time so the global EchoSuppressor slot stays consistent (each apply
          marks the suppressor before touching the clipboard, in on_clip)."""
          while True:
              kind, data, peer_name = await self._apply_queue.get()
              try:
                  await self._on_clip(kind, data, peer_name)
              except Exception as exc:
                  log.exception(f"apply handler failed: {exc}")

      # ---- inbound listener ----------------------------------------------
      async def serve(self) -> None:
          try:
              server = await asyncio.start_server(
                  self._handle_inbound, host="0.0.0.0", port=self.config.port,
              )
          except OSError as exc:
              if exc.errno == errno.EADDRINUSE:
                  raise FatalStartupError(
                      f"port {self.config.port} still in use after cleanup attempt; "
                      f"another process may have grabbed it -- try again or pick --port"
                  ) from exc
              raise
          log.info(f"listening on tcp/{self.config.port}")
          async with server:
              await server.serve_forever()

      async def _handle_inbound(self, reader, writer) -> None:
          peer = writer.get_extra_info("peername")
          log.debug(f"inbound from {peer}")
          peer_ip = peer[0] if peer else None
          # AuthGate IP-block check up front so failures aggregate per source IP
          # across connections that never form a link.
          if peer_ip and await self._auth_gate.is_blocked(peer_ip):
              log.info(
                  f"auth gate: {peer_ip} blocked "
                  f"(>{AuthGate.MAX_FAILS} failures, cooldown {AuthGate.COOLDOWN:.0f}s)"
              )
              _safe_close(writer)
              return
          _enable_keepalive(writer)
          try:
              await self._handshake_and_route(reader, writer, inbound=True)
          except Exception as exc:
              log.debug(f"inbound handshake failed: {exc}")
              _safe_close(writer)

      # ---- outbound dialer -----------------------------------------------
      async def ensure_link(self, host: str, port: int) -> None:
          """Dial a discovered peer if we have capacity and no live link to it.
          Returns promptly: on success the session serves in a background task."""
          if self.active_count() >= self.max_peers:
              return
          key = (host, port)
          if key in self._connecting:
              log.debug(f"connect to {host}:{port} already in flight, skipping")
              return
          if self.has_link_to_addr(host, port):
              return
          self._connecting.add(key)
          try:
              try:
                  reader, writer = await asyncio.wait_for(
                      asyncio.open_connection(host, port), timeout=CONNECT_TIMEOUT,
                  )
              except Exception as exc:
                  log.info(f"connect to {host}:{port} failed: {exc}")
                  return
              log.debug(f"outbound connected to {host}:{port}")
              _enable_keepalive(writer)
              try:
                  await self._handshake_and_route(
                      reader, writer, inbound=False, remote_addr=key,
                  )
              except Exception as exc:
                  log.debug(f"outbound handshake failed: {exc}")
                  _safe_close(writer)
          finally:
              self._connecting.discard(key)

      # ---- handshake + pre-routing gate ----------------------------------
      async def _handshake_and_route(self, reader, writer, inbound,
                                     remote_addr=None) -> None:
          ok = await _write_frame(writer, {
              "type": "hello",
              "token": self._token_hash,
              "node_id": self.node_id,
              "name": self.config.name,
              "version": PROTOCOL_VERSION,
              "app_version": APP_VERSION,
              "protocol_major": PROTOCOL_MAJOR,
              "protocol_minor": PROTOCOL_MINOR,
          }, timeout=SEND_TIMEOUT)
          if not ok:
              _safe_close(writer)
              return
          peer_ip_for_emit = ""
          try:
              peer = writer.get_extra_info("peername")
              if peer:
                  peer_ip_for_emit = peer[0]
          except Exception:
              pass
          try:
              hello = await asyncio.wait_for(_read_frame(reader), timeout=HANDSHAKE_TIMEOUT)
          except asyncio.TimeoutError:
              log.warning("handshake timeout")
              emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="timeout"))
              _safe_close(writer)
              return
          if not hello or hello.get("type") != "hello":
              log.warning("invalid hello, closing")
              emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason="invalid"))
              _safe_close(writer)
              return
          peer_ip = None
          if inbound:
              peer = writer.get_extra_info("peername")
              peer_ip = peer[0] if peer else None
          if hello.get("token") != self._token_hash:
              log.warning(f"auth failed from peer name={hello.get('name')!r}")
              if peer_ip:
                  await self._auth_gate.record_fail(peer_ip)
              emit_event(HandshakeFailed(addr=peer_ip or peer_ip_for_emit, reason="auth"))
              _safe_close(writer)
              return
          # Version parse with backward-compat defaults (old peer: only `version`).
          peer_proto_major_raw = hello.get("protocol_major")
          if not isinstance(peer_proto_major_raw, int):
              peer_proto_major_raw = hello.get("version", 0)
              if not isinstance(peer_proto_major_raw, int):
                  peer_proto_major_raw = 0
          peer_proto_minor_raw = hello.get("protocol_minor", 0)
          if not isinstance(peer_proto_minor_raw, int):
              peer_proto_minor_raw = 0
          peer_app_version = hello.get("app_version")
          if not isinstance(peer_app_version, str) or not peer_app_version:
              peer_app_version = "unknown"
          peer_version = VersionInfo(
              app_version=peer_app_version,
              protocol_major=peer_proto_major_raw,
              protocol_minor=peer_proto_minor_raw,
          )
          local_version = VersionInfo(
              app_version=APP_VERSION,
              protocol_major=PROTOCOL_MAJOR,
              protocol_minor=PROTOCOL_MINOR,
          )
          compat = negotiate(local_version, peer_version)
          if not link_allowed(compat):
              log.warning(
                  f"version refused: local proto={PROTOCOL_MAJOR}.{PROTOCOL_MINOR} "
                  f"app={APP_VERSION} vs peer proto="
                  f"{peer_version.protocol_major}.{peer_version.protocol_minor} "
                  f"app={peer_version.app_version} -> {compat.value}"
              )
              emit_event(HandshakeFailed(addr=peer_ip_for_emit, reason=f"version:{compat.value}"))
              _safe_close(writer)
              return
          if compat != Compatibility.COMPATIBLE:
              log.info(
                  f"version mismatch (link kept): {compat.value} "
                  f"local proto={PROTOCOL_MAJOR}.{PROTOCOL_MINOR} vs peer proto="
                  f"{peer_version.protocol_major}.{peer_version.protocol_minor}"
              )
          peer_id = hello.get("node_id")
          if not isinstance(peer_id, str) or peer_id == self.node_id:
              log.debug("self loopback or bad node_id, dropping")
              _safe_close(writer)
              return
          if peer_ip:
              await self._auth_gate.record_ok(peer_ip)
          peer_name = str(hello.get("name") or peer_id[:8])
          link = await self._route(
              peer_id, peer_name, peer_proto_minor_raw, peer_version.app_version,
              reader, writer, inbound, remote_addr,
          )
          if link is None:
              _safe_close(writer)
              return
          # Serve in the background so the dialer/accept loop moves on to other
          # peers. run_recv owns the writer for the rest of the link's life.
          asyncio.create_task(self._serve_link(link, peer_id))

      # ---- routing: replacement / tie-break / cap ------------------------
      def _keep_new(self, inbound: bool, peer_id: str) -> bool:
          """Existing tie-break rule for the genuine simultaneous-connect race:
          smaller node_id keeps its OUTBOUND end, larger keeps its INBOUND end."""
          return ((not inbound and self.node_id < peer_id) or
                  (inbound and self.node_id > peer_id))

      async def _route(self, peer_id, peer_name, minor, app_version,
                       reader, writer, inbound, remote_addr):
          """Under the lock, decide accept/replace/refuse for an authenticated
          connection and, on accept, install a PeerLink in the table. Returns
          the installed PeerLink, or None if refused/dropped (caller closes)."""
          async with self._lock:
              existing = self._links.get(peer_id)
              if existing is not None:
                  # Known node_id: reconnect/duplicate/race. Never refused by
                  # the cap. The tie-break applies only inside the race window;
                  # otherwise a fresh authenticated handshake for a live link
                  # means the peer considers the old link dead -> replace it.
                  if existing.active:
                      race = (time.monotonic() - existing.linked_at) < RACE_WINDOW_S
                      if race and not self._keep_new(inbound, peer_id):
                          log.debug("tie-breaker: dropping duplicate link (race)")
                          return None
                      if race:
                          log.debug("tie-breaker: replacing existing link (race)")
                      else:
                          log.info(
                              f"replacing link with {existing.peer_name!r} "
                              f"(peer reconnected)"
                          )
                  await existing.close()
                  self._links.pop(peer_id, None)
              else:
                  # New node_id -> enforce the peer cap.
                  if len(self._links) >= self.max_peers:
                      log.info(
                          f"peer cap reached ({len(self._links)}); refusing {peer_name!r}"
                      )
                      return None
              link = PeerLink(
                  self.node_id, peer_id, peer_name, minor, reader, writer,
                  self._enqueue_received, remote_addr=remote_addr,
              )
              self._links[peer_id] = link
          log.info(
              f"linked with peer name={peer_name!r} id={peer_id[:8]} "
              f"({'inbound' if inbound else 'outbound'}) app_version={app_version} "
              f"peer_proto={PROTOCOL_MAJOR}.{minor}"
          )
          emit_event(LinkUp(
              node_id=peer_id, peer_name=peer_name,
              app_version=app_version, protocol_minor=minor,
          ))
          return link

      async def _serve_link(self, link, peer_id) -> None:
          """Run one link's receive loop + its own per-link staleness watchdog.
          On teardown, remove the table entry (freeing a cap slot), emit
          LinkDown, and re-scan discovery to re-admit a waiting peer."""
          ping_task = asyncio.create_task(link_ping_loop(link))
          try:
              await link.run_recv()
          finally:
              ping_task.cancel()
              try:
                  await ping_task
              except asyncio.CancelledError:
                  pass
              removed = False
              async with self._lock:
                  # Identity guard: a replacement already swapped the table
                  # entry, so the replaced link must NOT emit LinkDown.
                  if self._links.get(peer_id) is link:
                      del self._links[peer_id]
                      removed = True
              if removed:
                  log.info(f"peer {link.peer_name!r} disconnected")
                  emit_event(LinkDown(node_id=peer_id, reason="peer disconnected"))
                  if self._beacon is not None:
                      await self.redial_discovered(self._beacon)

      async def _drop_link(self, link) -> None:
          """Force-close one link (used on a broadcast send failure)."""
          await link.close()

      # ---- broadcast -----------------------------------------------------
      async def broadcast_clip(self, kind, content) -> None:
          """Fan out a simple (text/image/file) clip to all active links; a
          per-link failure drops only that link."""
          for link in list(self._links.values()):
              if not link.active:
                  continue
              try:
                  await link.send_clip(kind, content)
              except Exception as exc:
                  log.info(f"send to {link.peer_name!r} failed: {exc}; dropping link")
                  await self._drop_link(link)

      async def broadcast_files(self, data) -> tuple:
          """Fan out a multi-file selection with per-link minor gating. Returns
          (sent_full, sent_fallback, max_dropped) aggregated across links for a
          single toast. The global echo check is done by the caller."""
          sent_full = sent_fallback = max_dropped = 0
          for link in list(self._links.values()):
              if not link.active:
                  continue
              try:
                  decision, n = await send_files_to_link(link, data)
              except Exception as exc:
                  log.info(f"send to {link.peer_name!r} failed: {exc}; dropping link")
                  await self._drop_link(link)
                  continue
              if decision == "files":
                  sent_full += 1
              else:  # "file" legacy fallback for a minor-0 peer
                  sent_fallback += 1
                  max_dropped = max(max_dropped, n)
          return sent_full, sent_fallback, max_dropped

      # ---- re-admission --------------------------------------------------
      async def redial_discovered(self, beacon) -> None:
          """Dial every discovered peer we are not yet linked to, up to the cap.
          Called on link drop and each retry cycle. Keyed by the advertised
          node_id (== the peer's hello node_id); addresses already backing a
          live link are skipped."""
          linked_addrs = {l.remote_addr for l in self._links.values()
                          if l.active and l.remote_addr}
          for nid, (host, port) in list(beacon.known_peers.items()):
              if nid == self.node_id:
                  continue
              if self.active_count() >= self.max_peers:
                  break
              existing = self._links.get(nid)
              if existing is not None and existing.active:
                  continue
              if (host, port) in linked_addrs:
                  continue
              await self.ensure_link(host, port)

      async def close(self) -> None:
          """Drop every active link. Safe to call multiple times."""
          for link in list(self._links.values()):
              await link.close()
          self._links.clear()
  ```

- [ ] **Step 4f: Add `send_files_to_link` + refactor `emit_files_clip`** — Edit `anyclip.py`. Anchor on the current `emit_files_clip` (lines 2200-2218):
  ```python
  async def emit_files_clip(link, suppressor, data) -> tuple:
      """Decide how to send a local multi-file selection to the peer and do it.
      ``data`` is [(name, raw_bytes), ...] with len >= 2. Returns:
        ("suppressed", 0) -- echo of a just-received set; nothing sent.
        ("files", n)      -- sent all n files as one kind:"files" clip.
        ("file", dropped) -- peer protocol_minor 0; sent the first file as a
                             legacy kind:"file" clip; ``dropped`` others not sent.
      """
      hashes = [sha256_bytes(bytes(raw)) for _name, raw in data]
      aggregate = aggregate_files_hash(hashes)
      if not suppressor.should_send("files", aggregate):
          return ("suppressed", 0)
      minor = link.peer_protocol_minor or 0
      if minor >= 1:
          await link.send_clip("files", data)
          return ("files", len(data))
      first_name, first_raw = data[0]
      await link.send_clip("file", (first_name, bytes(first_raw)))
      return ("file", len(data) - 1)
  ```
  Replace with:
  ```python
  async def send_files_to_link(link, data) -> tuple:
      """Per-link minor gating for a multi-file clip (NO echo check). Reused by
      the mesh broadcast loop so gating is evaluated per link:
        minor >= 1 -> one kind:"files" clip, returns ("files", len(data)).
        minor 0    -> first file as legacy kind:"file", returns ("file", dropped).
      """
      minor = link.peer_protocol_minor or 0
      if minor >= 1:
          await link.send_clip("files", data)
          return ("files", len(data))
      first_name, first_raw = data[0]
      await link.send_clip("file", (first_name, bytes(first_raw)))
      return ("file", len(data) - 1)


  async def emit_files_clip(link, suppressor, data) -> tuple:
      """Single-link send decision + echo suppression. ``data`` is
      [(name, raw_bytes), ...] with len >= 2. Returns:
        ("suppressed", 0) -- echo of a just-received set; nothing sent.
        ("files", n)      -- sent all n files as one kind:"files" clip.
        ("file", dropped) -- peer protocol_minor 0; sent the first file as a
                             legacy kind:"file" clip; ``dropped`` others not sent.
      """
      hashes = [sha256_bytes(bytes(raw)) for _name, raw in data]
      aggregate = aggregate_files_hash(hashes)
      if not suppressor.should_send("files", aggregate):
          return ("suppressed", 0)
      return await send_files_to_link(link, data)
  ```

- [ ] **Step 4g: Rewire `on_remote_clip` for the source-peer name** — Edit `anyclip.py`. Anchor on the current `on_remote_clip` header + its `peer` binding (lines 2236-2237):
  ```python
      async def on_remote_clip(kind: str, data) -> None:
          peer = link.peer_name or "peer"
  ```
  Replace with:
  ```python
      async def on_remote_clip(kind: str, data, peer_name: str = "peer") -> None:
          peer = peer_name or "peer"
  ```
  (The rest of `on_remote_clip` is unchanged — it reads the local `peer` variable, which now comes from the delivering link via the serialized apply queue.)

- [ ] **Step 4h: Construct the manager instead of a single link** — Edit `anyclip.py`. Anchor on line 2301:
  ```python
      link = PeerLink(config, node_id, on_remote_clip)
  ```
  Replace with:
  ```python
      manager = LinkManager(config, node_id, on_remote_clip, max_peers=config.max_peers)
  ```

- [ ] **Step 4i: Rewrite `on_local_change` to broadcast through the manager** — Edit `anyclip.py`. Anchor on the entire current `on_local_change` closure (lines 2303-2373):
  ```python
      async def on_local_change(kind: str, data) -> None:
          if not link.active:
              return
          if kind == "text":
              assert isinstance(data, str)
              if not suppressor.should_send("text", sha256_hex(data)):
                  log.debug("skip echo of just-received text")
                  return
              await link.send_clip("text", data)
              peer = link.peer_name or "peer"
              log.info(f"-> sent text {len(data)} chars to {peer!r}")
              if notify_enabled:
                  await notify_async(
                      title=f"AnyClip → {peer}",
                      message=preview(data),
                  )
          elif kind == "image":
              assert isinstance(data, (bytes, bytearray))
              png = bytes(data)
              if not suppressor.should_send("image", sha256_bytes(png)):
                  log.debug("skip echo of just-received image")
                  return
              await link.send_clip("image", png)
              peer = link.peer_name or "peer"
              log.info(f"-> sent image {len(png)} bytes to {peer!r}")
              if notify_enabled:
                  await notify_async(
                      title=f"AnyClip → {peer}",
                      message=f"image ({len(png)//1024} KB)",
                  )
          elif kind == "file":
              assert isinstance(data, tuple) and len(data) == 2
              name, raw = data
              raw_b = bytes(raw)
              if not suppressor.should_send("file", sha256_bytes(raw_b)):
                  log.debug("skip echo of just-received file")
                  return
              await link.send_clip("file", (name, raw_b))
              peer = link.peer_name or "peer"
              log.info(f"-> sent file {name!r} {len(raw_b)} bytes to {peer!r}")
              if notify_enabled:
                  await notify_async(
                      title=f"AnyClip → {peer}",
                      message=f"file: {name} ({len(raw_b)//1024} KB)",
                  )
          elif kind == "files":
              assert isinstance(data, list)
              decision, count = await emit_files_clip(link, suppressor, data)
              peer = link.peer_name or "peer"
              if decision == "suppressed":
                  log.debug("skip echo of just-received files")
                  return
              if decision == "files":
                  log.info(f"-> sent {count} files to {peer!r}")
                  if notify_enabled:
                      await notify_async(
                          title=f"AnyClip → {peer}", message=f"{count} files",
                      )
              else:  # "file" old-peer fallback
                  log.info(
                      f"-> sent 1 file to {peer!r} "
                      f"(peer proto minor 0, {count} dropped)"
                  )
                  if notify_enabled:
                      await notify_async(
                          title=f"AnyClip → {peer}", message="file (1 of many)",
                      )
                  await on_file_skipped(
                      f"{count} file(s) not sent — peer needs an update for "
                      "multi-file sync"
                  )
  ```
  Replace with:
  ```python
      async def on_local_change(kind: str, data) -> None:
          # Broadcast one local clip to EVERY active link. The watcher and the
          # EchoSuppressor stay global (content-hash based); the should_send
          # check gates the whole broadcast once, before the fan-out.
          if manager.active_count() == 0:
              return
          if kind == "text":
              assert isinstance(data, str)
              if not suppressor.should_send("text", sha256_hex(data)):
                  log.debug("skip echo of just-received text")
                  return
              await manager.broadcast_clip("text", data)
              log.info(f"-> sent text {len(data)} chars to {manager.active_count()} peer(s)")
              if notify_enabled:
                  await notify_async(title="AnyClip →", message=preview(data))
          elif kind == "image":
              assert isinstance(data, (bytes, bytearray))
              png = bytes(data)
              if not suppressor.should_send("image", sha256_bytes(png)):
                  log.debug("skip echo of just-received image")
                  return
              await manager.broadcast_clip("image", png)
              log.info(f"-> sent image {len(png)} bytes to {manager.active_count()} peer(s)")
              if notify_enabled:
                  await notify_async(title="AnyClip →", message=f"image ({len(png)//1024} KB)")
          elif kind == "file":
              assert isinstance(data, tuple) and len(data) == 2
              name, raw = data
              raw_b = bytes(raw)
              if not suppressor.should_send("file", sha256_bytes(raw_b)):
                  log.debug("skip echo of just-received file")
                  return
              await manager.broadcast_clip("file", (name, raw_b))
              log.info(f"-> sent file {name!r} {len(raw_b)} bytes to {manager.active_count()} peer(s)")
              if notify_enabled:
                  await notify_async(
                      title="AnyClip →", message=f"file: {name} ({len(raw_b)//1024} KB)",
                  )
          elif kind == "files":
              assert isinstance(data, list)
              # Global echo check once; per-link minor gating inside the loop.
              hashes = [sha256_bytes(bytes(raw)) for _name, raw in data]
              aggregate = aggregate_files_hash(hashes)
              if not suppressor.should_send("files", aggregate):
                  log.debug("skip echo of just-received files")
                  return
              sent_full, sent_fallback, max_dropped = await manager.broadcast_files(data)
              total = sent_full + sent_fallback
              if total == 0:
                  return
              log.info(
                  f"-> sent files to {total} peer(s) "
                  f"({sent_full} full, {sent_fallback} first-file fallback)"
              )
              if notify_enabled:
                  await notify_async(title="AnyClip →", message=f"{len(data)} files")
              # One aggregated skip toast across ALL peers that could only take
              # the first file (protocol_minor 0). Same principle as the
              # folder-skip aggregation in d8894a0.
              if max_dropped > 0:
                  await on_file_skipped(
                      f"{max_dropped} file(s) not sent to {sent_fallback} peer(s) — "
                      "they need an update for multi-file sync"
                  )
  ```

- [ ] **Step 4j: Point the beacon at the manager's dialer** — Edit `anyclip.py`. Anchor on line 2383:
  ```python
      beacon = MdnsBeacon(config, node_id, link.try_connect)
  ```
  Replace with:
  ```python
      beacon = MdnsBeacon(config, node_id, manager.ensure_link)
      manager.attach_beacon(beacon)
  ```

  > **Scope boundary:** stop here. The coroutine list at lines 2388-2404 and the `finally` at 2405-2421 still reference the old single `link` binding; they are rewritten in Task 3, which also re-keys the watchdogs. `run()` is intentionally not runnable at this commit — it is validated only by the `tests/test_link_manager.py` unit tests, which drive `LinkManager` directly.

- [ ] **Step 5: Run tests to verify they pass** — `source .venv/bin/activate && pytest tests/test_link_manager.py tests/test_receive_files.py tests/test_wire_files.py tests/test_send_timeout.py tests/test_link_liveness.py -v`
  Expected: all pass. In particular `test_wire_files.py` (constructs a bare `PeerLink` via `__new__`, sets `_writer`/`_send_timeout`/`_send`, calls `send_clip`) and `test_send_timeout.py` / `test_link_liveness.py` (also `__new__`-based, and `link_ping_loop` unchanged) are unaffected by the constructor split; `test_receive_files.py`'s emit-files tests still pass because `emit_files_clip` keeps the same return tuples.

- [ ] **Step 6: Full suite sanity** — `source .venv/bin/activate && pytest tests/ -v`
  Expected: all green (no test executes `anyclip.run()`, so the temporarily-inconsistent coroutine list does not fail collection or any test).

- [ ] **Step 7: Commit**
  ```
  git add anyclip.py tests/test_link_manager.py tests/test_receive_files.py
  git commit -m "$(cat <<'EOF'
  feat(mesh): split PeerLink into LinkManager + per-peer PeerLink

  LinkManager owns the listening socket, the active-link table keyed by
  node_id, the pre-routing gate (AuthGate/token/version/self-drop run after
  the hello exchange), the cap (DEFAULT_MAX_PEERS=8, --max-peers), broadcast
  fan-out with per-link minor gating, and a serialized receive-apply queue.
  PeerLink narrows to one post-hello session. Duplicate connections replace
  the live session; the tie-break applies only inside the race window.

  run() is rewired to construct the manager and broadcast local clips; the
  coroutine list + watchdog re-keying land in the follow-up.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

---

### Task 3: watchdog re-keying, `run()` coros wiring, and GUI shells

**Scope note:** Task 2 delivered the `LinkManager` core and rewired the `run()` handler closures + manager/beacon construction, but deliberately left the coroutine list and the `finally` block referencing the old single `link`. This task finishes `run()`: re-keys the global mDNS escalator on the manager's active-link count, converts the discovery retry loops to feed N links, wires the coros, then renders the mesh peer list in both GUI shells and plumbs the `max_peers` default. After this task `run()` is runnable end-to-end again.

**Files:**
- Modify: `/Users/seojeonghwa/project/AnyClip/anyclip.py`
  - `idle_link_watchdog` (lines 2003-2045): take the manager, key on `active_count() == 0`
  - `mdns_reconnect_loop` (lines 2074-2145): take the manager, dial up to the cap
  - `peer_keepalive` (lines 2148-2169): take the manager, dial via `ensure_link`
  - `run()` coroutine list (lines 2388-2404) + `finally` (lines 2405-2421): wire the manager, drop the global `link_ping_loop`
- Modify: `/Users/seojeonghwa/project/AnyClip/app/menubar_mac.py`
  - `_build_config` (lines 44-52): add `max_peers`
  - `_apply_state` linked branch (line 178): render the sorted peer list
- Modify: `/Users/seojeonghwa/project/AnyClip/app/tray_win.py`
  - `_build_config` (lines 91-99): add `max_peers`
  - `_status_label` (lines 221-229): render the sorted peer list
- Test: `/Users/seojeonghwa/project/AnyClip/tests/test_link_manager.py` (append the global-escalator test)

**Interfaces:**
- Consumes: `LinkManager.active_count()` / `ensure_link()` / `redial_discovered()` / `has_link_to_addr()` / `close()` (Task 2), `link_ping_loop(link)` (unchanged), `MdnsBeacon` (`known_peers`, `address_fails`, `refresh`), `MAX_RECONNECT_FAILS`, `DEFAULT_MAX_PEERS`; `peer_state.status_label(state)` (Task 1).
- Produces:
  - `idle_link_watchdog(beacon, manager, idle_threshold=60.0, refresh_attempts_before_bounce=3)`
  - `mdns_reconnect_loop(beacon, manager)`
  - `peer_keepalive(host, port, manager)`
  - `run()` coroutine wiring using the manager
  - shells rendering `peer_state.status_label(state)`; shell `Config` construction passing `max_peers`

- [ ] **Step 1: Write the failing test** — append to `tests/test_link_manager.py`:
  ```python
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
  ```

- [ ] **Step 2: Run the test to verify it fails** — `source .venv/bin/activate && pytest tests/test_link_manager.py::test_idle_link_watchdog_fires_only_at_zero_active_links -v`
  Expected: fails — `idle_link_watchdog` still has the old `(beacon, link, ...)` signature and keys on `link.active`, so calling it with a manager raises `AttributeError: 'LinkManager' object has no attribute 'active'`.

- [ ] **Step 3a: Re-key `idle_link_watchdog` on the manager** — Edit `anyclip.py`. Anchor on lines 2003-2045:
  ```python
  async def idle_link_watchdog(
      beacon: "MdnsBeacon",
      link: "PeerLink",
      idle_threshold: float = 60.0,
      refresh_attempts_before_bounce: int = 3,
  ) -> None:
      """Self-heal mDNS when the link sits dead for too long.

      network_watchdog only fires on IP change. If Wi-Fi blips but the
      IP survives, zeroconf's multicast socket can end up silently
      unbound (no Errno, no exception) and stop delivering peer
      advertisements. mdns_reconnect_loop can't help because it depends
      on `known_peers`, which were pruned the last time the link died.

      Recovery escalation:
        1..refresh_attempts: call beacon.refresh() to re-announce + re-issue
           the browse query. Cheap; reuses the existing AsyncZeroconf.
        attempts+1: raise RuntimeError to unwind asyncio.gather() and let
           the supervisor restart the whole runtime with a fresh zeroconf
           socket. Same trick as network_watchdog.

      Counter resets whenever the link comes back up.
      """
      consecutive_idle = 0
      while True:
          await asyncio.sleep(idle_threshold)
          if link.active:
              consecutive_idle = 0
              continue
          consecutive_idle += 1
          elapsed = idle_threshold * consecutive_idle
          if consecutive_idle <= refresh_attempts_before_bounce:
              log.info(
                  f"link idle {elapsed:.0f}s; refreshing mDNS "
                  f"(attempt {consecutive_idle}/{refresh_attempts_before_bounce})"
              )
              await beacon.refresh()
          else:
              raise RuntimeError(
                  f"link idle > {elapsed:.0f}s with no recovery after "
                  f"{refresh_attempts_before_bounce} mDNS refresh attempts; "
                  f"bouncing daemon to re-bind zeroconf"
              )
  ```
  Replace with:
  ```python
  async def idle_link_watchdog(
      beacon: "MdnsBeacon",
      manager: "LinkManager",
      idle_threshold: float = 60.0,
      refresh_attempts_before_bounce: int = 3,
  ) -> None:
      """Self-heal mDNS when the WHOLE mesh sits dead for too long.

      network_watchdog only fires on IP change. If Wi-Fi blips but the IP
      survives, zeroconf's multicast socket can end up silently unbound (no
      Errno, no exception) and stop delivering peer advertisements.
      mdns_reconnect_loop can't help because it depends on `known_peers`,
      which were pruned the last time the links died.

      Global scope, keyed on the manager's ACTIVE-LINK COUNT (never on a
      single link's idleness): keyed per-link, one sleeping peer would bounce
      the daemon and tear down every healthy link. Only when ZERO links are
      active do we escalate.

      Recovery escalation:
        1..refresh_attempts: call beacon.refresh() to re-announce + re-issue
           the browse query. Cheap; reuses the existing AsyncZeroconf.
        attempts+1: raise RuntimeError to unwind asyncio.gather() and let the
           supervisor restart the whole runtime with a fresh zeroconf socket.

      Counter resets whenever any link is active.
      """
      consecutive_idle = 0
      while True:
          await asyncio.sleep(idle_threshold)
          if manager.active_count() > 0:
              consecutive_idle = 0
              continue
          consecutive_idle += 1
          elapsed = idle_threshold * consecutive_idle
          if consecutive_idle <= refresh_attempts_before_bounce:
              log.info(
                  f"no active links for {elapsed:.0f}s; refreshing mDNS "
                  f"(attempt {consecutive_idle}/{refresh_attempts_before_bounce})"
              )
              await beacon.refresh()
          else:
              raise RuntimeError(
                  f"no active links > {elapsed:.0f}s with no recovery after "
                  f"{refresh_attempts_before_bounce} mDNS refresh attempts; "
                  f"bouncing daemon to re-bind zeroconf"
              )
  ```

- [ ] **Step 3b: Convert `mdns_reconnect_loop` to feed N links** — Edit `anyclip.py`. Anchor on the entire current function (lines 2074-2145):
  ```python
  async def mdns_reconnect_loop(beacon: "MdnsBeacon", link: "PeerLink") -> None:
      """Retry mDNS-discovered peers when the link drops.

      The zeroconf browser only fires ServiceStateChange.Added on first
      sight. If a TCP link dies (e.g. the OS reassigns our IP and we hit
      EADDRNOTAVAIL on send) but the peer keeps advertising, no new event
      arrives -- so the only chance to reconnect is to remember every peer
      we ever resolved and poll them ourselves.

      Backoff is the same shape as peer_keepalive (1s -> 60s, reset after a
      session that survived 5s). Cheap when the link is up: just a 2s sleep.
      """
      backoff = 1.0
      while True:
          if link.active:
              backoff = 1.0
              await asyncio.sleep(2)
              continue
          # Dedup by (host, port). The same physical peer can leave several
          # stale entries in known_peers because every restart of the remote
          # daemon mints a new node_id, but the address stays the same -- we
          # only need to attempt one outbound per address per cycle.
          peers = list(dict.fromkeys(beacon.known_peers.values()))
          if not peers:
              await asyncio.sleep(2)
              continue
          # Try every known peer in turn; stop early if one of them links up.
          attempted = False
          for host, port in peers:
              if link.active:
                  break
              attempted = True
              start = time.monotonic()
              await link.try_connect(host, port)
              elapsed = time.monotonic() - start
              if link.active:
                  # Successful link -- clear any failure history for this addr.
                  beacon.address_fails.pop((host, port), None)
                  if elapsed > 5.0:
                      backoff = 1.0
                  break
              # Link is not active *right now*, but if the call took longer
              # than 5 s the handshake clearly succeeded and the session was
              # up for a real time before dropping. That is a healthy peer
              # whose link happened to die after the fact (a tie-breaker
              # winner taking over, a transient network blip, ...) so we
              # explicitly do NOT count it toward the prune threshold.
              if elapsed > 5.0:
                  beacon.address_fails.pop((host, port), None)
                  continue
              # Real fast-fail (no route, refused, tie-breaker drop in ms).
              fails = beacon.address_fails.get((host, port), 0) + 1
              beacon.address_fails[(host, port)] = fails
              if fails >= MAX_RECONNECT_FAILS:
                  stale_ids = [
                      nid for nid, addr in beacon.known_peers.items()
                      if addr == (host, port)
                  ]
                  for nid in stale_ids:
                      beacon.known_peers.pop(nid, None)
                  beacon.address_fails.pop((host, port), None)
                  log.info(
                      f"pruned stale peer address {host}:{port} after "
                      f"{fails} failed attempts; awaiting fresh mDNS discovery"
                  )
          if link.active:
              continue
          if attempted:
              await asyncio.sleep(min(backoff, 60))
              backoff = min(backoff * 2, 60)
          else:
              await asyncio.sleep(2)
  ```
  Replace with:
  ```python
  async def mdns_reconnect_loop(beacon: "MdnsBeacon", manager: "LinkManager") -> None:
      """Retry mDNS-discovered peers to keep the mesh filled up to the cap.

      The zeroconf browser only fires ServiceStateChange.Added on first sight.
      If a TCP link dies (e.g. the OS reassigns our IP and we hit
      EADDRNOTAVAIL on send) but the peer keeps advertising, no new event
      arrives -- so the only chance to reconnect is to remember every peer we
      ever resolved and poll them ourselves. In the mesh this feeds N links:
      dial every known address not already backing a live link, up to the cap.

      Backoff is the same shape as peer_keepalive (1s -> 60s, reset after a
      session that survived 5s). Cheap when the mesh is full: just a 2s sleep.
      """
      backoff = 1.0
      while True:
          if manager.active_count() >= manager.max_peers:
              backoff = 1.0
              await asyncio.sleep(2)
              continue
          # Dedup by (host, port). The same physical peer can leave several
          # stale entries in known_peers (every remote restart mints a new
          # node_id at the same address) -- one outbound per address per cycle.
          peers = list(dict.fromkeys(beacon.known_peers.values()))
          if not peers:
              await asyncio.sleep(2)
              continue
          attempted = False
          for host, port in peers:
              if manager.active_count() >= manager.max_peers:
                  break
              if manager.has_link_to_addr(host, port):
                  continue
              attempted = True
              start = time.monotonic()
              await manager.ensure_link(host, port)
              elapsed = time.monotonic() - start
              if manager.has_link_to_addr(host, port):
                  # Linked -- clear any failure history for this addr.
                  beacon.address_fails.pop((host, port), None)
                  if elapsed > 5.0:
                      backoff = 1.0
                  continue
              # Not linked right now, but a >5s call means the handshake
              # succeeded and the session ran before dropping -- a healthy peer
              # whose link died after the fact. Do NOT count it toward pruning.
              if elapsed > 5.0:
                  beacon.address_fails.pop((host, port), None)
                  continue
              # Real fast-fail (no route, refused, over-cap short-circuit, ...).
              fails = beacon.address_fails.get((host, port), 0) + 1
              beacon.address_fails[(host, port)] = fails
              if fails >= MAX_RECONNECT_FAILS:
                  stale_ids = [
                      nid for nid, addr in beacon.known_peers.items()
                      if addr == (host, port)
                  ]
                  for nid in stale_ids:
                      beacon.known_peers.pop(nid, None)
                  beacon.address_fails.pop((host, port), None)
                  log.info(
                      f"pruned stale peer address {host}:{port} after "
                      f"{fails} failed attempts; awaiting fresh mDNS discovery"
                  )
          if attempted:
              await asyncio.sleep(min(backoff, 60))
              backoff = min(backoff * 2, 60)
          else:
              await asyncio.sleep(2)
  ```

- [ ] **Step 3c: Convert `peer_keepalive` to the manager dialer** — Edit `anyclip.py`. Anchor on lines 2148-2169:
  ```python
  async def peer_keepalive(host: str, port: int, link: "PeerLink") -> None:
      """Maintain an outbound connection to a manually-configured peer.

      Coexists with mDNS-driven discovery: if the link is already active
      (from either source), this just polls until it drops, then retries
      with exponential backoff (1s -> 60s cap). Backoff resets after a
      session that lasted >5s (treated as 'real' uptime).
      """
      backoff = 1.0
      while True:
          if link.active:
              backoff = 1.0
              await asyncio.sleep(2)
              continue
          start = time.monotonic()
          await link.try_connect(host, port)
          elapsed = time.monotonic() - start
          if elapsed > 5.0:
              backoff = 1.0
              continue
          await asyncio.sleep(min(backoff, 60))
          backoff = min(backoff * 2, 60)
  ```
  Replace with:
  ```python
  async def peer_keepalive(host: str, port: int, manager: "LinkManager") -> None:
      """Maintain an outbound connection to a manually-configured peer.

      Coexists with mDNS-driven discovery: if a live link to this address
      already exists (from either source), this just polls until it drops,
      then retries with exponential backoff (1s -> 60s cap). Backoff resets
      after a session that lasted >5s (treated as 'real' uptime).
      """
      backoff = 1.0
      while True:
          if manager.has_link_to_addr(host, port):
              backoff = 1.0
              await asyncio.sleep(2)
              continue
          start = time.monotonic()
          await manager.ensure_link(host, port)
          elapsed = time.monotonic() - start
          if elapsed > 5.0:
              backoff = 1.0
              continue
          await asyncio.sleep(min(backoff, 60))
          backoff = min(backoff * 2, 60)
  ```

- [ ] **Step 3d: Wire the manager into `run()`'s coroutine list + shutdown** — Edit `anyclip.py`. Anchor on lines 2388-2421 (the coroutine list, `gather`, and the `finally`):
  ```python
      tasks: list[asyncio.Task] = []
      try:
          await beacon.start()
          coros = [
              link.serve(),
              watcher.run(),
              mdns_reconnect_loop(beacon, link),
              network_watchdog(beacon),
              idle_link_watchdog(beacon, link),
              link_ping_loop(link),
          ]
          for host, port in config.peers:
              coros.append(peer_keepalive(host, port, link))
          if sys.platform == "darwin":
              coros.append(_run_permission_probe(beacon))
          tasks = [asyncio.create_task(c) for c in coros]
          await asyncio.gather(*tasks)
      finally:
          # Cancel any siblings still running and *await* them so their
          # finally blocks complete before the event loop closes. Without
          # this, asyncio.gather() re-raises on the first failure without
          # draining peers, leaving Windows ProactorEventLoop's internal
          # IocpProactor.accept coroutine pending -- which surfaces as
          # "Task was destroyed but it is pending!" at loop shutdown.
          for t in tasks:
              if not t.done():
                  t.cancel()
          if tasks:
              await asyncio.gather(*tasks, return_exceptions=True)
          await link.close()
          await beacon.stop()
          release_pid_lock()
          clear_received_dir()
  ```
  Replace with:
  ```python
      tasks: list[asyncio.Task] = []
      try:
          await beacon.start()
          coros = [
              manager.serve(),
              manager.apply_loop(),
              watcher.run(),
              mdns_reconnect_loop(beacon, manager),
              network_watchdog(beacon),
              idle_link_watchdog(beacon, manager),
          ]
          # The per-link staleness dropper (link_ping_loop) is now started
          # per link inside LinkManager._serve_link, not once globally here.
          for host, port in config.peers:
              coros.append(peer_keepalive(host, port, manager))
          if sys.platform == "darwin":
              coros.append(_run_permission_probe(beacon))
          tasks = [asyncio.create_task(c) for c in coros]
          await asyncio.gather(*tasks)
      finally:
          # Cancel any siblings still running and *await* them so their
          # finally blocks complete before the event loop closes. Without
          # this, asyncio.gather() re-raises on the first failure without
          # draining peers, leaving Windows ProactorEventLoop's internal
          # IocpProactor.accept coroutine pending -- which surfaces as
          # "Task was destroyed but it is pending!" at loop shutdown.
          for t in tasks:
              if not t.done():
                  t.cancel()
          if tasks:
              await asyncio.gather(*tasks, return_exceptions=True)
          await manager.close()
          await beacon.stop()
          release_pid_lock()
          clear_received_dir()
  ```

- [ ] **Step 4: Run the anyclip tests to verify green** — `source .venv/bin/activate && pytest tests/test_link_manager.py tests/test_link_liveness.py -v`
  Expected: all pass, including `test_idle_link_watchdog_fires_only_at_zero_active_links` (refreshes stay 0 with one active link; escalates to `RuntimeError` at zero active links) and the unchanged `test_link_liveness.py` (per-link `link_ping_loop` signature is untouched).

- [ ] **Step 5a: Render the mesh list in the macOS menubar** — Edit `app/menubar_mac.py`. First the linked branch of `_apply_state` (lines 177-181). Anchor on:
  ```python
          if kind == "linked":
              self.status_item.title = f"Linked: {state.peer_name or 'peer'}"
              self.last_sync_item.title = (
                  f"Linked since: {time.strftime('%H:%M:%S')}"
              )
              self._remove_lan_settings_item()
  ```
  Replace with:
  ```python
          if kind == "linked":
              self.status_item.title = peer_state.status_label(state)
              self.last_sync_item.title = (
                  f"Linked since: {time.strftime('%H:%M:%S')}"
              )
              self._remove_lan_settings_item()
  ```
  (`peer_state` is already imported in this module — it is the type of the `state` argument.)

- [ ] **Step 5b: Add `max_peers` to the macOS `_build_config`** — Edit `app/menubar_mac.py`. Anchor on lines 44-52:
  ```python
      return anyclip.Config(
          token=token,
          port=anyclip.DEFAULT_PORT,
          name=socket.gethostname(),
          poll_interval=0.5,
          verbose=False,
          peers=[],
          no_notify=False,
      )
  ```
  Replace with:
  ```python
      return anyclip.Config(
          token=token,
          port=anyclip.DEFAULT_PORT,
          name=socket.gethostname(),
          poll_interval=0.5,
          verbose=False,
          peers=[],
          no_notify=False,
          max_peers=anyclip.DEFAULT_MAX_PEERS,
      )
  ```

- [ ] **Step 5c: Render the mesh list in the Windows tray** — Edit `app/tray_win.py`. Anchor on the `_status_label` linked branch (lines 221-229):
  ```python
      def _status_label(self) -> str:
          s = self._current_state
          if s.kind == "linked":
              return f"Linked: {s.peer_name or 'peer'}"
          if s.kind == "searching":
              return "Searching for peer"
          if s.kind == "error":
              return f"Error: {s.reason or 'unknown'}"
          return "Idle"
  ```
  Replace with:
  ```python
      def _status_label(self) -> str:
          return peer_state.status_label(self._current_state)
  ```
  (`peer_state` is already imported in this module — `state_queue` is typed `Queue[peer_state.State]`.)

- [ ] **Step 5d: Add `max_peers` to the Windows `_build_config`** — Edit `app/tray_win.py`. Anchor on lines 91-99:
  ```python
      return anyclip.Config(
          token=token,
          port=anyclip.DEFAULT_PORT,
          name=socket.gethostname(),
          poll_interval=0.5,
          verbose=False,
          peers=[],
          no_notify=False,
      )
  ```
  Replace with:
  ```python
      return anyclip.Config(
          token=token,
          port=anyclip.DEFAULT_PORT,
          name=socket.gethostname(),
          poll_interval=0.5,
          verbose=False,
          peers=[],
          no_notify=False,
          max_peers=anyclip.DEFAULT_MAX_PEERS,
      )
  ```

- [ ] **Step 6: Verify import-safety of the shells + full suite** — `source .venv/bin/activate && python -c "import ast; ast.parse(open('app/menubar_mac.py').read()); ast.parse(open('app/tray_win.py').read()); print('shells parse OK')" && pytest tests/ -v`
  Expected: `shells parse OK` (the shells import rumps/pystray so they are not imported by the test suite; a parse check catches syntax slips), then the full suite green. Manual verification of the shells' live rendering (two peers → "Linked: macbook, win-pc") is a launch-time check, not a unit test.

- [ ] **Step 7: Commit**
  ```
  git add anyclip.py app/menubar_mac.py app/tray_win.py tests/test_link_manager.py
  git commit -m "$(cat <<'EOF'
  feat(mesh): global escalator keyed on active-link count + mesh UI

  idle_link_watchdog now keys on the manager's active-link count (fires only
  at zero active links, so one sleeping peer can't bounce the daemon);
  mdns_reconnect_loop / peer_keepalive dial up to the cap through the manager;
  run() wires manager.serve/apply_loop and drops the global link_ping_loop
  (now per link). Both shells render the sorted mesh peer list via
  peer_state.status_label, and carry the max_peers config default.

  Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
  EOF
  )"
  ```

---

## Cross-task dependency summary (Python)

- **Task 2 consumes from Task 1:** `LinkUp(node_id, peer_name, app_version, protocol_minor)` and `LinkDown(node_id, reason)` (emitted in `LinkManager._route` / `_serve_link`).
- **Task 3 consumes from Task 1:** `peer_state.status_label(state)` (both shells).
- **Task 3 consumes from Task 2:** `LinkManager.active_count()` / `ensure_link()` / `redial_discovered()` / `has_link_to_addr()` / `close()` / `serve()` / `apply_loop()` / `.max_peers`; `Config.max_peers` / `DEFAULT_MAX_PEERS`.

---

# Part 2 — Swift (Tasks 4–6)


**Goal:** Turn the shipped Mac app from a single-link daemon into a full-mesh
multi-peer daemon (spec `docs/superpowers/specs/2026-07-22-desktop-multipeer-design.md`).
Every device links directly to every discovered same-token peer (up to a cap of
8), local clips broadcast to **all** links, received clips apply locally with
**no relay**. **No wire change** (protocol stays 1.1; golden vectors and interop
fixtures untouched). App version 1.3.0 comes from the release tag — no version
constants change.

**Tech Stack:** Swift 6 toolchain, SPM package `formacOS/`, `.swiftLanguageMode(.v5)`.
Build/test: `swift test --package-path formacOS`.

## Global constraints (every task)

- **Cross-implementation event contract (FIXED):** `DaemonEvent.linkUp(nodeID: String, peerName: String)`,
  `DaemonEvent.linkDown(nodeID: String, reason: String)`; `PeerUIState.peers: [String: String]`
  (node_id → display name). Reducer: LinkUp inserts/updates by node_id, LinkDown
  removes ONLY that node_id, state is "linked" iff `peers` is non-empty. Zero
  peers → existing "searching" presentation unchanged; ≥1 → status text
  `"Linked: " + names sorted ordinally, comma-joined`.
- **Cap:** `LinkManager.defaultMaxPeers = 8`. No `config.json` change (token-only).
  No native `--max-peers` flag (Python-only).
- **Suppressor stays global** (content-hash), not per-link. Receive applies are
  serialized through ONE queue across all links; each apply marks the suppressor
  BEFORE touching the clipboard.
- ⚠️ **Environment warning:** running the Swift daemon/interop suites on this Mac
  can flip the live AnyClip menu-bar app into a sticky false auth-error state.
  Known local artifact, not a failure — restart AnyClip.app to clear it.
- **Commits** end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
  Work on branch `feat/desktop-multipeer`.

## Task ordering & compile-coupling (read before starting)

Swift compiles a whole target at once, and these changes span one tightly-coupled
compile unit. The three tasks below are therefore **strictly ordered** and each
leaves the full package green:

- **Task 4** (Core `PeerState.swift`) is self-contained except for a small
  mechanical ripple (two emit lines in `PeerLink.swift`, one line in
  `MenuIconTests.swift`) needed to keep the package compiling. A back-compat
  `PeerUIState.peerName` computed accessor keeps `StatusItemController` compiling
  untouched.
- **Task 5** replaces the single-link `PeerLink` with `LinkManager` + a narrowed
  `PeerLink`, and rewires `Daemon.swift`, `Watchdogs.swift`, and `MdnsBeacon.swift`.
  Because narrowing `PeerLink` removes the fat server/dial API that
  `Daemon`/`Watchdogs` depend on, **all** of these land together (they will not
  compile piecemeal). `PeerLinkTests.swift` is deleted and its coverage moves to
  the new `LinkManagerTests.swift`.
- **Task 6** consumes the multi-peer state (StatusItemController rendering) and adds
  the two-`fake_peer` mesh/non-relay interop test.

Do not run `swift test` mid-task expecting green until the task's implement steps
are complete — the package is briefly non-compiling between steps within a task.

---

### Task 4: Core event model + peers-keyed reducer (`PeerState.swift`)

**Files:**
- Modify: `formacOS/Sources/AnyClipCore/PeerState.swift` (whole file rewrite)
- Modify: `formacOS/Sources/AnyClipDaemon/PeerLink.swift` — emit sites at line 320 and line 359 (mechanical; these lines are removed entirely in Task 5)
- Modify: `formacOS/Tests/AnyClipCoreTests/MenuIconTests.swift` — line 5 constructor call
- Test: `formacOS/Tests/AnyClipCoreTests/PeerStateTests.swift` (whole file rewrite)

**Interfaces:**
- Consumes: `handshakeFailThreshold` (existing, kept).
- Produces:
  - `enum DaemonEvent`: `.linkUp(nodeID: String, peerName: String)`, `.linkDown(nodeID: String, reason: String)` (other cases unchanged: `.peerDiscovered(name:addr:)`, `.permissionMissing(kind:)`, `.handshakeFailed(addr:reason:)`)
  - `struct PeerUIState`: stored `kind: Kind`, `peers: [String: String]`, `since: Double?`, `reason: String?`, `consecutiveHandshakeFails: Int`; computed `sortedPeerNames: [String]`, `peerName: String?` (back-compat = first sorted)
  - `PeerUIState.init(kind:peers:since:reason:consecutiveHandshakeFails:)` with `peers` defaulting to `[:]`
  - `PeerUIState.initial`
  - `func reducePeerState(_:_:now:) -> PeerUIState`

- [ ] **Step 1: Write the failing test** — replace the whole of `formacOS/Tests/AnyClipCoreTests/PeerStateTests.swift`:
```swift
import Testing
@testable import AnyClipCore

@Test func initialIsIdleWithNoPeers() {
    #expect(PeerUIState.initial.kind == .idle)
    #expect(PeerUIState.initial.peers.isEmpty)
    #expect(PeerUIState.initial.sortedPeerNames.isEmpty)
}

@Test func linkUpAddsPeerAndGoesLinked() {
    let s = reducePeerState(.initial, .linkUp(nodeID: "abc", peerName: "win-pc"), now: 42.0)
    #expect(s.kind == .linked)
    #expect(s.peers == ["abc": "win-pc"])
    #expect(s.since == 42.0)
    #expect(s.consecutiveHandshakeFails == 0)
    #expect(s.peerName == "win-pc")            // back-compat accessor
}

@Test func twoPeersRenderSortedNames() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "n2", peerName: "win-pc"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "n1", peerName: "android-9"), now: 2)
    #expect(s.kind == .linked)
    #expect(s.peers.count == 2)
    #expect(s.sortedPeerNames == ["android-9", "win-pc"])  // ordinal sort by name
    #expect(s.since == 1)                                  // "since first peer" preserved
}

@Test func linkDownRemovesOnlyThatPeer() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "a", peerName: "p-a"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "b", peerName: "p-b"), now: 2)
    s = reducePeerState(s, .linkDown(nodeID: "a", reason: "peer disconnected"), now: 3)
    #expect(s.kind == .linked)                 // b still linked
    #expect(s.peers == ["b": "p-b"])
}

@Test func linkDownOfLastPeerGoesSearching() {
    let linked = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    let s = reducePeerState(linked, .linkDown(nodeID: "x", reason: "peer disconnected"), now: 2)
    #expect(s.kind == .searching)
    #expect(s.reason == "peer disconnected")
    #expect(s.peers.isEmpty)
}

@Test func discoveryMovesIdleToSearching() {
    let s = reducePeerState(.initial, .peerDiscovered(name: "n", addr: "1.2.3.4:24816"), now: 1)
    #expect(s.kind == .searching)
}

@Test func discoveryMovesErrorToSearching() {
    let err = reducePeerState(.initial, .permissionMissing(kind: "local_network"), now: 1)
    let s = reducePeerState(err, .peerDiscovered(name: "n", addr: "a"), now: 2)
    #expect(s.kind == .searching)
}

@Test func discoveryDoesNotFlapLinked() {
    let linked = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    let s = reducePeerState(linked, .peerDiscovered(name: "n", addr: "a"), now: 2)
    #expect(s == linked)
}

@Test func permissionMissingIsError() {
    let s = reducePeerState(.initial, .permissionMissing(kind: "local_network"), now: 1)
    #expect(s.kind == .error)
    #expect(s.reason == "local_network")
}

@Test func fiveHandshakeFailsTripAuthErrorWhenNoPeers() {
    var s = PeerUIState.initial
    for i in 1...(handshakeFailThreshold - 1) {
        s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: Double(i))
        #expect(s.kind == .idle)
        #expect(s.consecutiveHandshakeFails == i)
    }
    s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: 5)
    #expect(s.kind == .error)
    #expect(s.reason == "auth")
}

@Test func handshakeFailsDoNotTripErrorWhileAPeerIsLinked() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    for i in 1...(handshakeFailThreshold + 2) {
        s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: Double(i))
    }
    #expect(s.kind == .linked)                 // an existing link masks the auth escalation
    #expect(s.peers == ["x": "p"])
}

@Test func linkUpResetsFailCounter() {
    var s = PeerUIState.initial
    s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "x", peerName: "p"), now: 2)
    #expect(s.kind == .linked)
    #expect(s.consecutiveHandshakeFails == 0)
}
```

- [ ] **Step 2: Run test to verify it fails** — `swift test --package-path formacOS --filter PeerStateTests`
  Expected: compile failure — `DaemonEvent` has no `.linkUp(nodeID:peerName:)` / `.linkDown(nodeID:reason:)`, `PeerUIState` has no `peers`/`sortedPeerNames`. (The package currently uses `.linkUp(peerName:peerID:)`.)

- [ ] **Step 3a: Rewrite `PeerState.swift`** — replace the whole of `formacOS/Sources/AnyClipCore/PeerState.swift`:
```swift
/// Daemon-event types and pure state-machine reducer for the UI shell.
/// Port of peer_state.py. Multi-peer: LinkUp/LinkDown carry a stable node_id,
/// and the UI state holds a peer collection keyed by node_id (full mesh).

public enum DaemonEvent: Sendable, Equatable {
    case peerDiscovered(name: String, addr: String)
    case linkUp(nodeID: String, peerName: String)
    case linkDown(nodeID: String, reason: String)
    case handshakeFailed(addr: String, reason: String)
    case permissionMissing(kind: String)
}

public struct PeerUIState: Sendable, Equatable {
    public enum Kind: String, Sendable { case idle, searching, linked, error }

    public var kind: Kind
    /// node_id -> display name for every currently-linked peer. Source of truth
    /// for the linked/searching split: linked iff this is non-empty.
    public var peers: [String: String]
    public var since: Double?
    public var reason: String?
    /// Internal bookkeeping so the reducer can trip into error("auth") after a
    /// run of failed handshakes while NO peer is linked. UI reads
    /// kind/peers/since/reason.
    public var consecutiveHandshakeFails: Int

    public init(
        kind: Kind,
        peers: [String: String] = [:],
        since: Double? = nil,
        reason: String? = nil,
        consecutiveHandshakeFails: Int = 0
    ) {
        self.kind = kind
        self.peers = peers
        self.since = since
        self.reason = reason
        self.consecutiveHandshakeFails = consecutiveHandshakeFails
    }

    /// Linked peer display names, ordinally sorted. The status line renders
    /// "Linked: " + these joined by ", ". Empty when not linked.
    public var sortedPeerNames: [String] { peers.values.sorted() }

    /// Back-compat single-name accessor (first sorted peer). Prefer
    /// sortedPeerNames for multi-peer callers.
    public var peerName: String? { sortedPeerNames.first }

    public static let initial = PeerUIState(kind: .idle)
}

public let handshakeFailThreshold = 5

public func reducePeerState(
    _ prev: PeerUIState, _ event: DaemonEvent, now: Double
) -> PeerUIState {
    switch event {
    case .permissionMissing(let kind):
        return PeerUIState(kind: .error, reason: kind)
    case .linkUp(let nodeID, let peerName):
        var next = prev
        next.peers[nodeID] = peerName
        next.kind = .linked
        // "Linked since" tracks the first peer of the current linked run.
        next.since = prev.peers.isEmpty ? now : prev.since
        next.reason = nil
        next.consecutiveHandshakeFails = 0
        return next
    case .linkDown(let nodeID, let reason):
        var next = prev
        next.peers[nodeID] = nil
        if next.peers.isEmpty {
            // Last peer gone -> back to the unchanged "searching" presentation.
            return PeerUIState(
                kind: .searching, reason: reason,
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        next.kind = .linked   // other peers remain; stay linked, keep `since`
        return next
    case .peerDiscovered:
        if prev.kind == .idle || prev.kind == .error {
            return PeerUIState(kind: .searching)
        }
        return prev
    case .handshakeFailed:
        var next = prev
        next.consecutiveHandshakeFails += 1
        // An established link masks the auth escalation: one stranger failing
        // auth must not flip a working multi-peer UI into error.
        if next.consecutiveHandshakeFails >= handshakeFailThreshold && next.peers.isEmpty {
            return PeerUIState(
                kind: .error, reason: "auth",
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        return next
    }
}
```

- [ ] **Step 3b: Mechanical ripple in `PeerLink.swift`** — keep the package compiling (both lines are deleted wholesale in Task 5). Edit line 320:
  - Old: `        emit?(.linkUp(peerName: displayName, peerID: peerID))`
  - New: `        emit?(.linkUp(nodeID: peerID, peerName: displayName))`

  Then the teardown emit. Anchor on lines 350-360:
```swift
        let wasActive = (activeConn === framed)
        if wasActive {
            activeConn = nil
            peerNodeID = nil
            peerName = nil
            peerProtocolMinor = 0
        }
        AnyLog.shared.info("peer disconnected")
        if wasActive {
            emit?(.linkDown(reason: "peer disconnected"))
        }
```
Replace with (capture the id before clearing it):
```swift
        let wasActive = (activeConn === framed)
        let downID = peerNodeID
        if wasActive {
            activeConn = nil
            peerNodeID = nil
            peerName = nil
            peerProtocolMinor = 0
        }
        AnyLog.shared.info("peer disconnected")
        if wasActive {
            emit?(.linkDown(nodeID: downID ?? "", reason: "peer disconnected"))
        }
```

- [ ] **Step 3c: Mechanical ripple in `MenuIconTests.swift`** — edit line 5:
  - Old: `    let s = reducePeerState(.initial, .linkUp(peerName: "p", peerID: "x"), now: 1)`
  - New: `    let s = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)`

- [ ] **Step 4: Run tests to verify they pass** — `swift test --package-path formacOS`
  Expected: whole suite green. `PeerStateTests` (12 tests) pass; `MenuIconTests` still passes (reads only `state.kind`); `StatusItemController` compiles unchanged via the back-compat `peerName` accessor; existing `PeerLinkTests` still compiles (`if case .linkUp = $0` is label-free) and passes.

- [ ] **Step 5: Commit**
```
git add formacOS/Sources/AnyClipCore/PeerState.swift formacOS/Sources/AnyClipDaemon/PeerLink.swift formacOS/Tests/AnyClipCoreTests/PeerStateTests.swift formacOS/Tests/AnyClipCoreTests/MenuIconTests.swift
git commit -m "$(cat <<'EOF'
feat(core): multi-peer event model + peers-keyed UI reducer

DaemonEvent.linkUp/linkDown gain a stable node_id; PeerUIState holds a
peers dict (node_id -> name) and reduces add/remove by node_id (linked iff
non-empty) instead of collapsing to searching on any linkDown. Back-compat
peerName accessor keeps the current status render compiling.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 5: LinkManager full-mesh routing + broadcast + serialized applies

Replaces the single-link `PeerLink` server half with `LinkManager` (listener,
active-link table keyed by node_id, pre-routing gate, cap, broadcast), narrows
`PeerLink` to one post-hello session, and rewires `Daemon`/`Watchdogs`/`MdnsBeacon`.
This is one compile unit — everything below lands before `swift test` runs green.

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/LinkManager.swift`
- Modify: `formacOS/Sources/AnyClipDaemon/PeerLink.swift` (whole file rewrite → narrowed)
- Modify: `formacOS/Sources/AnyClipDaemon/Watchdogs.swift` — `idleLinkWatchdog` (lines 59-83) and `mdnsReconnectLoop` (lines 87-134) re-keyed to the manager; `linkPingLoop` and `networkWatchdog` unchanged
- Modify: `formacOS/Sources/AnyClipDaemon/MdnsBeacon.swift` — `onPeer` closure type + `ingest` (line 74) + `peersSnapshot` (lines 99-107)
- Modify: `formacOS/Sources/AnyClipDaemon/Daemon.swift` — runOnce body lines 131 → EOF
- Modify: `formacOS/Tests/AnyClipDaemonTests/MdnsBeaconTests.swift` — `makeBeacon` (line 8)
- Create: `formacOS/Tests/AnyClipDaemonTests/LinkManagerTests.swift`
- Delete: `formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift` (coverage moves to LinkManagerTests)

**Interfaces:**
- Consumes: `DaemonEvent.linkUp(nodeID:peerName:)`, `.linkDown(nodeID:reason:)` (Task 4); `downgradeForPeer(_:peerMinor:)` (existing, `Daemon.swift:56`); `linkPingLoop(link:interval:deadFactor:)` (existing, unchanged); `FramedConnection`, `withTimeout`, `TimeoutError`, `FatalStartupError`, `Locked`, `monotonicNow`, `WireConnectionError`; Core `AuthGate`, `negotiate`, `linkAllowed`, `VersionInfo`, `sha256Hex`, `strictBase64Decode`, `decodeFileEntries`, `Wire.*`, `WireMessage.hello/clip/pong/ping`.
- Produces (consumed by Task 6):
  - `actor LinkManager` with:
    - `init(config: LinkManager.LinkConfig, nodeID: String, maxPeers: Int = defaultMaxPeers, pingInterval: Double = 30, pingDeadFactor: Double = 3)`
    - `static let defaultMaxPeers = 8`
    - `struct LinkConfig(token:port:name:appVersion:)`
    - `func setHandlers(onClip: @escaping @Sendable (ClipPayload, String) async -> Void, emit: @escaping @Sendable (DaemonEvent) -> Void)`
    - `func serve() async throws`, `var isServing: Bool`, `func configureAdvertising(instanceName:txtData:)`, `func reAnnounce()`, `func shutdown()`
    - `func tryConnect(to: NWEndpoint, label: String) async -> ConnectOutcome`
    - `func broadcast(_ payload: ClipPayload) async -> BroadcastResult`
    - `func activeLinkCount() -> Int`, `func hasLink(nodeID: String) -> Bool`, `var atCap: Bool`
  - `enum ConnectOutcome: Sendable { case routed, failed, atCap, busy }`
  - `struct BroadcastResult: Sendable { var delivered: [(peerName: String, payload: ClipPayload)]; var maxDropped: Int }`
  - narrowed `actor PeerLink`: `init(conn:peerNodeID:peerName:peerProtocolMinor:onClip:)`, `nonisolated let peerNodeID/peerName/peerProtocolMinor`, `func run() async`, `func sendClip(_:) async -> Bool`, `func sendPing() async`, `func secondsSinceInbound() -> Double?`, `func dropStaleLink(idleSeconds:)`, `nonisolated func close()`, `var isActive: Bool`
  - `func idleLinkWatchdog(beacon: MdnsBeacon, manager: LinkManager, idleThreshold:refreshAttempts:) async throws`
  - `func mdnsReconnectLoop(beacon: MdnsBeacon, manager: LinkManager) async throws`
  - `MdnsBeacon.peersSnapshot() -> [(endpoint: NWEndpoint, label: String, peerID: String)]`
  - `MdnsBeacon.init(...onPeer: @escaping @Sendable (NWEndpoint, String, String) async -> Void)`

- [ ] **Step 1: Write the failing tests** — create `formacOS/Tests/AnyClipDaemonTests/LinkManagerTests.swift`:
```swift
import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeManager(
    token: String, port: UInt16, name: String,
    clips: Locked<[(ClipPayload, String)]>, events: Locked<[DaemonEvent]>,
    maxPeers: Int = LinkManager.defaultMaxPeers,
    pingInterval: Double = 30, pingDeadFactor: Double = 3
) async -> LinkManager {
    let m = LinkManager(
        config: LinkManager.LinkConfig(
            token: token, port: port, name: name, appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased(),
        maxPeers: maxPeers, pingInterval: pingInterval, pingDeadFactor: pingDeadFactor)
    await m.setHandlers(
        onClip: { payload, peer in clips.set(clips.get() + [(payload, peer)]) },
        emit: { event in events.set(events.get() + [event]) })
    return m
}

private func waitUntil(_ timeout: Double = 5.0, _ cond: @escaping () async -> Bool) async -> Bool {
    let deadline = monotonicNow() + timeout
    while monotonicNow() < deadline {
        if await cond() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return await cond()
}

/// Drive a raw peer against a serving manager: TCP connect + hello handshake,
/// returning the connected FramedConnection (already routed by the manager).
private func rawHandshake(
    port: UInt16, token: String, nodeID: String, name: String,
    minor: Int = Wire.protocolMinor, major: Int = Wire.protocolMajor
) async throws -> FramedConnection {
    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await raw.start()
    var hello = WireMessage.hello(
        tokenHash: sha256Hex(token), nodeID: nodeID, name: name, appVersion: "0.0.0-test")
    hello.protocol_minor = minor
    hello.protocol_major = major
    try await raw.sendFrame(hello)
    _ = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }  // manager's hello
    return raw
}

@Test func twoManagersHandshakeAndBroadcastBothWays() async throws {
    let aClips = Locked<[(ClipPayload, String)]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[(ClipPayload, String)]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28471, name: "node-a", clips: aClips, events: aEvents)
    let b = await makeManager(token: "tok", port: 28472, name: "node-b", clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let outcome = await b.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28471)!), label: "a")
    #expect(outcome == .routed)
    #expect(await waitUntil { await a.activeLinkCount() == 1 && await b.activeLinkCount() == 1 })
    #expect(aEvents.get().contains { if case .linkUp = $0 { return true }; return false })

    _ = await b.broadcast(.text("from-b"))
    #expect(await waitUntil {
        aClips.get().contains { if case .text(let s) = $0.0 { return s == "from-b" }; return false }
    })
    _ = await a.broadcast(.image(Data([1, 2, 3])))
    #expect(await waitUntil {
        bClips.get().contains { if case .image(let d) = $0.0 { return d == Data([1, 2, 3]) }; return false }
    })
    await a.shutdown(); await b.shutdown()
}

@Test func wrongTokenIsRejectedWithAuthEvent() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "right", port: 28473, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28473, token: "wrong", nodeID: "ffffffff-bad", name: "b")
    defer { raw.cancel() }
    #expect(await waitUntil {
        events.get().contains { if case .handshakeFailed(_, "auth") = $0 { return true }; return false }
    })
    #expect(await a.activeLinkCount() == 0)
    await a.shutdown()
}

@Test func pingIsAnsweredWithPong() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28475, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28475, token: "tok", nodeID: "ffffffff-raw", name: "raw")
    defer { raw.cancel() }
    try await raw.sendFrame(.ping(ts: 1))
    let reply = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }
    #expect(reply?.type == "pong")
    await a.shutdown()
}

@Test func staleSilentLinkIsDropped() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    // Tight per-link ping: 3 missed 0.3s intervals = 0.9s silence -> drop.
    let a = await makeManager(token: "tok", port: 28479, name: "a", clips: clips, events: events,
                              pingInterval: 0.3, pingDeadFactor: 3)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28479, token: "tok", nodeID: "ffffffff-silent", name: "raw")
    defer { raw.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    // The raw peer never pongs; the per-link staleness dropper must reap it.
    #expect(await waitUntil(5) { await a.activeLinkCount() == 0 })
    #expect(events.get().contains { if case .linkDown = $0 { return true }; return false })
    await a.shutdown()
}

@Test func majorVersionMismatchIsRefused() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28476, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28476, token: "tok", nodeID: "ffffffff-v2", name: "future", major: 2)
    defer { raw.cancel() }
    #expect(await waitUntil {
        events.get().contains {
            if case .handshakeFailed(_, let r) = $0 { return r.hasPrefix("version:") }; return false
        }
    })
    #expect(await a.activeLinkCount() == 0)
    await a.shutdown()
}

@Test func serveRetriesBindWhenPortTemporarilyHeld() async throws {
    let port: UInt16 = 28477
    let blocker = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    blocker.newConnectionHandler = { $0.cancel() }
    blocker.start(queue: .global())
    try await Task.sleep(nanoseconds: 300_000_000)

    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let m = await makeManager(token: "t", port: port, name: "retry", clips: clips, events: events)
    let serveTask = Task { try await m.serve() }; defer { serveTask.cancel() }
    try await Task.sleep(nanoseconds: 700_000_000)
    blocker.cancel()
    #expect(await waitUntil(5) { await m.isServing })
    await m.shutdown()
}

@Test func newNodeCreatesLinkAndEmitsLinkUp() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28483, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28483, token: "tok", nodeID: "node-fresh", name: "fresh")
    defer { raw.cancel() }
    #expect(await waitUntil { await a.hasLink(nodeID: "node-fresh") })
    #expect(await a.activeLinkCount() == 1)
    #expect(events.get().contains {
        if case .linkUp(let id, let name) = $0 { return id == "node-fresh" && name == "fresh" }
        return false
    })
    await a.shutdown()
}

@Test func duplicateNodeReplacesLiveSession() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28484, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let first = try await rawHandshake(port: 28484, token: "tok", nodeID: "dup", name: "first")
    #expect(await waitUntil { await a.hasLink(nodeID: "dup") })
    // Sleep past the race window so this is a genuine replacement, not a tie-break.
    try await Task.sleep(nanoseconds: 1_700_000_000)
    let second = try await rawHandshake(port: 28484, token: "tok", nodeID: "dup", name: "second")
    defer { second.cancel() }
    // The old socket is closed by the manager; still exactly one link for "dup".
    #expect(await waitUntil { (try? await withTimeout(seconds: 2) { try await first.receiveMessage() }) == nil })
    #expect(await a.activeLinkCount() == 1)
    // The second connection is the live one: a broadcast reaches it.
    _ = await a.broadcast(.text("to-second"))
    let got = try await withTimeout(seconds: 5) { try await second.receiveMessage() }
    #expect(got?.kind == "text" && got?.content == "to-second")
    await a.shutdown()
}

@Test func overCapNewPeerRefusedKnownReconnectRouted() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28488, name: "a", clips: clips, events: events, maxPeers: 2)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let p1 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n1", name: "p1")
    let p2 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n2", name: "p2")
    defer { p1.cancel(); p2.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    // A NEW node at cap is refused (its socket is closed, no link created).
    let p3 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n3", name: "p3")
    defer { p3.cancel() }
    #expect(await waitUntil { (try? await withTimeout(seconds: 2) { try await p3.receiveMessage() }) == nil })
    #expect(await a.activeLinkCount() == 2)
    #expect(!(await a.hasLink(nodeID: "n3")))

    // A KNOWN node reconnecting at cap is routed (replacement), never refused.
    try await Task.sleep(nanoseconds: 1_700_000_000)
    let p1b = try await rawHandshake(port: 28488, token: "tok", nodeID: "n1", name: "p1-again")
    defer { p1b.cancel() }
    _ = await a.broadcast(.text("to-p1-again"))
    let got = try await withTimeout(seconds: 5) { try await p1b.receiveMessage() }
    #expect(got?.content == "to-p1-again")
    #expect(await a.activeLinkCount() == 2)
    await a.shutdown()
}

@Test func deadLinkFreesCapSlot() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28489, name: "a", clips: clips, events: events, maxPeers: 1)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let x = try await rawHandshake(port: 28489, token: "tok", nodeID: "x", name: "x")
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    x.cancel()                                            // peer vanishes
    #expect(await waitUntil { await a.activeLinkCount() == 0 })   // slot freed
    #expect(events.get().contains {
        if case .linkDown(let id, _) = $0 { return id == "x" }; return false
    })
    let y = try await rawHandshake(port: 28489, token: "tok", nodeID: "y", name: "y")
    defer { y.cancel() }
    #expect(await waitUntil { await a.hasLink(nodeID: "y") })     // re-admitted
    await a.shutdown()
}

@Test func broadcastFansOutAndIsolatesFailure() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28490, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let p1 = try await rawHandshake(port: 28490, token: "tok", nodeID: "n1", name: "p1")
    let p2 = try await rawHandshake(port: 28490, token: "tok", nodeID: "n2", name: "p2")
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let result = await a.broadcast(.text("fanout"))
    #expect(result.delivered.count == 2)
    let g1 = try await withTimeout(seconds: 5) { try await p1.receiveMessage() }
    let g2 = try await withTimeout(seconds: 5) { try await p2.receiveMessage() }
    #expect(g1?.content == "fanout" && g2?.content == "fanout")

    // Drop p2's socket; a broadcast failure to it must drop ONLY that link.
    p2.cancel()
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    _ = await a.broadcast(.text("after-drop"))
    let g1b = try await withTimeout(seconds: 5) { try await p1.receiveMessage() }
    #expect(g1b?.content == "after-drop")
    #expect(await a.hasLink(nodeID: "n1"))
    p1.cancel()
    await a.shutdown()
}

@Test func perLinkMinorGatingFilesVsFallback() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28492, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawHandshake(port: 28492, token: "tok", nodeID: "old", name: "old", minor: 0)
    let modern = try await rawHandshake(port: 28492, token: "tok", nodeID: "new", name: "new", minor: 1)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let files: [(name: String, data: Data)] = [
        (name: "a.txt", data: Data("one".utf8)), (name: "b.txt", data: Data("two".utf8))]
    let result = await a.broadcast(.files(files))
    #expect(result.maxDropped == 1)   // the minor-0 peer left one file behind

    let gOld = try await withTimeout(seconds: 5) { try await old.receiveMessage() }
    #expect(gOld?.kind == "file")                       // legacy first-file fallback
    #expect(gOld?.name == "a.txt")
    let gNew = try await withTimeout(seconds: 5) { try await modern.receiveMessage() }
    #expect(gNew?.kind == "files")                      // full multi-file
    #expect(gNew?.files?.count == 2)
    await a.shutdown()
}

@Test func idleWatchdogFiresOnlyAtZeroActiveLinks() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let beacon = MdnsBeacon(nodeID: "self", emit: { _ in }, onPeer: { _, _, _ in })

    // Zero links + tight thresholds -> the global escalator bounces the daemon.
    let idle = await makeManager(token: "tok", port: 28493, name: "a", clips: clips, events: events)
    var threw = false
    do { try await idleLinkWatchdog(beacon: beacon, manager: idle, idleThreshold: 0.2, refreshAttempts: 1) }
    catch is DaemonRestartError { threw = true } catch {}
    #expect(threw)
    await idle.shutdown()

    // With an active link, activeLinkCount > 0 resets it: no throw in the window.
    let live = await makeManager(token: "tok", port: 28494, name: "b", clips: clips, events: events)
    let serve = Task { try await live.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await live.isServing })
    let raw = try await rawHandshake(port: 28494, token: "tok", nodeID: "peer", name: "peer")
    defer { raw.cancel() }
    #expect(await waitUntil { await live.activeLinkCount() == 1 })
    let wd = Task { try await idleLinkWatchdog(beacon: beacon, manager: live, idleThreshold: 0.2, refreshAttempts: 1) }
    try await Task.sleep(nanoseconds: 1_000_000_000)
    #expect(!wd.isCancelled ? true : true)              // still running (never threw)
    wd.cancel()
    await live.shutdown()
}
```

- [ ] **Step 2: Run tests to verify they fail** — `swift test --package-path formacOS --filter LinkManagerTests`
  Expected: compile failure — no `LinkManager` type, no `ConnectOutcome`/`BroadcastResult`, `MdnsBeacon.init` onPeer arity mismatch. (The whole target fails to build.)

- [ ] **Step 3a: Rewrite `PeerLink.swift` (narrowed)** — replace the whole of `formacOS/Sources/AnyClipDaemon/PeerLink.swift`:
```swift
import Foundation
import Network
import AnyClipCore

/// One peer pair: the session lifecycle from the POST-hello point. The
/// listening socket, outbound dialing, hello exchange, gate, and routing now
/// live in LinkManager; a PeerLink is constructed with an already-handshaked
/// FramedConnection and the parsed hello, and never re-reads a hello. Port of
/// the narrowed anyclip.PeerLink.
public actor PeerLink {
    /// Peer identity from the handed-over hello — immutable for the link's
    /// lifetime, so nonisolated lets the broadcast loop read them without await.
    public nonisolated let peerNodeID: String
    public nonisolated let peerName: String
    /// Peer's advertised protocol minor; gates the outbound files/kind:"file"
    /// downgrade in LinkManager's broadcast loop.
    public nonisolated let peerProtocolMinor: Int

    private let conn: FramedConnection
    private let onClip: @Sendable (ClipPayload, String) async -> Void
    /// Monotonic timestamp of the last inbound frame. Drives half-open
    /// detection: a slept/vanished peer keeps the socket "writable" yet sends
    /// nothing back, so staleness is judged from inbound silence.
    private var lastInboundAt: Double = monotonicNow()
    private var closed = false

    public init(
        conn: FramedConnection, peerNodeID: String, peerName: String,
        peerProtocolMinor: Int,
        onClip: @escaping @Sendable (ClipPayload, String) async -> Void
    ) {
        self.conn = conn
        self.peerNodeID = peerNodeID
        self.peerName = peerName
        self.peerProtocolMinor = peerProtocolMinor
        self.onClip = onClip
    }

    public var isActive: Bool { !closed }

    /// Receive loop. Returns when the socket EOFs/errors or close() cancels it.
    /// Emits NO lifecycle events — LinkManager owns link up/down.
    public func run() async {
        while !closed {
            let msg: WireMessage?
            do { msg = try await conn.receiveMessage() } catch { break }
            lastInboundAt = monotonicNow()
            guard let m = msg else { break }
            switch m.type {
            case "clip":
                await handleClip(m)
            case "ping":
                do { try await conn.sendFrame(.pong(ts: Date().timeIntervalSince1970)) }
                catch { AnyLog.shared.info("send failed (link likely down): \(error)") }
            case "pong":
                break
            default:
                AnyLog.shared.debug("ignoring message type: \(m.type)")
            }
        }
        closed = true
        AnyLog.shared.info("peer disconnected name=\(peerName) id=\(peerNodeID.prefix(8))")
    }

    private func handleClip(_ m: WireMessage) async {
        let kind = m.kind ?? "text"
        switch kind {
        case "text":
            if let content = m.content { await onClip(.text(content), peerName) }
        case "image":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad image payload from peer"); return
            }
            await onClip(.image(data), peerName)
        case "file":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad file payload from peer"); return
            }
            let name = (m.name?.isEmpty == false) ? m.name! : "received.bin"
            await onClip(.file(name: name, data: data), peerName)
        case "files":
            guard let entries = decodeFileEntries(m.files) else {
                AnyLog.shared.warning(
                    "bad files payload from peer (empty or invalid base64); dropping frame")
                return
            }
            await onClip(.files(entries), peerName)
        default:
            AnyLog.shared.debug("ignoring clip with kind=\(kind)")
        }
    }

    /// Per-link broadcast send. Returns false only on a "link likely down"
    /// error, so the caller drops this link; an oversize payload keeps the link.
    public func sendClip(_ payload: ClipPayload) async -> Bool {
        if closed { return false }
        let msg = WireMessage.clip(payload, ts: Date().timeIntervalSince1970)
        do { try await conn.sendFrame(msg); return true }
        catch let error as WireFrameError {
            AnyLog.shared.warning("payload too large, dropping: \(error)"); return true
        }
        catch {
            AnyLog.shared.info("send failed (link likely down): \(error)"); return false
        }
    }

    /// App-layer keepalive; drives traffic so a silently-dead socket surfaces.
    public func sendPing() async {
        if closed { return }
        do { try await conn.sendFrame(.ping(ts: Date().timeIntervalSince1970)) }
        catch { AnyLog.shared.info("send failed (link likely down): \(error)") }
    }

    /// Seconds since the last inbound frame, or nil once closed.
    public func secondsSinceInbound() -> Double? {
        closed ? nil : monotonicNow() - lastInboundAt
    }

    /// Drop a half-open link (peer slept/vanished): cancelling the connection
    /// wakes the parked receive, run() tears down, and LinkManager reaps it.
    public func dropStaleLink(idleSeconds: Double) {
        guard !closed else { return }
        AnyLog.shared.info(
            "link to \(peerName) idle \(Int(idleSeconds))s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect")
        closed = true
        conn.cancel()
    }

    /// Cancel the underlying connection from any isolation domain (routing /
    /// broadcast / shutdown call this synchronously). run() observes the
    /// cancelled socket and sets `closed`.
    public nonisolated func close() {
        conn.cancel()
    }
}
```

- [ ] **Step 3b: Create `LinkManager.swift`** — create `formacOS/Sources/AnyClipDaemon/LinkManager.swift`:
```swift
import Foundation
import Network
import AnyClipCore

/// Sentinel thrown internally when bind fails with EADDRINUSE; triggers a retry.
private struct PortInUseError: Error {}

/// Outcome of an outbound dial; consumed by mdnsReconnectLoop's fail bookkeeping.
public enum ConnectOutcome: Sendable {
    case routed   // handshake succeeded (link created, replaced, or tie-broken)
    case failed   // handshake failed (auth/version/timeout/connect)
    case atCap    // skipped: already at the peer cap
    case busy     // skipped: a dial to this address is already in flight
}

/// Per-copy broadcast result: which peers got the (possibly downgraded) clip,
/// and the largest old-peer file-drop count for the aggregated fallback toast.
public struct BroadcastResult: Sendable {
    public var delivered: [(peerName: String, payload: ClipPayload)]
    public var maxDropped: Int
    public init(delivered: [(peerName: String, payload: ClipPayload)], maxDropped: Int) {
        self.delivered = delivered
        self.maxDropped = maxDropped
    }
}

/// Owns the listening socket, the active-link table (keyed by peer node_id), the
/// pre-routing gate, and the broadcast fan-out. Full-mesh replacement for the
/// single-link PeerLink server half. Port of anyclip.LinkManager — actor
/// isolation replaces the asyncio Lock; the routing/registration block runs with
/// NO awaits, so it is atomic exactly like the Python critical section.
public actor LinkManager {
    public static let defaultMaxPeers = 8

    public struct LinkConfig: Sendable {
        public var token: String
        public var port: UInt16
        public var name: String
        public var appVersion: String
        public init(token: String, port: UInt16, name: String, appVersion: String) {
            self.token = token
            self.port = port
            self.name = name
            self.appVersion = appVersion
        }
    }

    private struct LinkEntry {
        let link: PeerLink
        let task: Task<Void, Never>
        let linkedAt: Double
        let gen: Int
    }

    private let config: LinkConfig
    private let nodeID: String
    private let tokenHash: String
    private let maxPeers: Int
    private let pingInterval: Double
    private let pingDeadFactor: Double
    private var authGate: AuthGate

    private var onClip: (@Sendable (ClipPayload, String) async -> Void)?
    private var emit: (@Sendable (DaemonEvent) -> Void)?

    private var links: [String: LinkEntry] = [:]
    private var connecting: Set<String> = []
    private var linkGen = 0

    private var listener: NWListener?
    public private(set) var isServing = false
    private var advertiseService: NWListener.Service?

    public init(
        config: LinkConfig, nodeID: String,
        maxPeers: Int = defaultMaxPeers,
        pingInterval: Double = 30, pingDeadFactor: Double = 3
    ) {
        self.config = config
        self.nodeID = nodeID
        self.tokenHash = sha256Hex(config.token)
        self.maxPeers = maxPeers
        self.pingInterval = pingInterval
        self.pingDeadFactor = pingDeadFactor
        self.authGate = AuthGate()
    }

    public func setHandlers(
        onClip: @escaping @Sendable (ClipPayload, String) async -> Void,
        emit: @escaping @Sendable (DaemonEvent) -> Void
    ) {
        self.onClip = onClip
        self.emit = emit
    }

    // ---- link-table queries --------------------------------------------
    public func activeLinkCount() -> Int { links.count }
    public func hasLink(nodeID: String) -> Bool { links[nodeID] != nil }
    public var atCap: Bool { links.count >= maxPeers }

    // ---- advertising (Bonjour lives on the listener) -------------------
    public func configureAdvertising(instanceName: String, txtData: Data) {
        advertiseService = NWListener.Service(
            name: instanceName, type: Wire.serviceType, domain: nil, txtRecord: txtData)
    }

    public func reAnnounce() {
        guard let listener, let advertiseService else { return }
        listener.service = nil
        listener.service = advertiseService
        AnyLog.shared.debug("mDNS: re-announced service")
    }

    // ---- serve (moved from PeerLink) -----------------------------------
    public func serve() async throws {
        guard self.listener == nil else {
            throw FatalStartupError("serve() called twice on the same LinkManager")
        }
        var attempt = 0
        let listener = try await makeAndStartListener(attempt: &attempt)
        listener.stateUpdateHandler = nil
        isServing = true
        AnyLog.shared.info("listening on tcp/\(config.port)")
        defer {
            listener.cancel()
            self.listener = nil
            isServing = false
        }
        while true { try await Task.sleep(nanoseconds: 1_000_000_000) }
    }

    private func makeAndStartListener(attempt: inout Int) async throws -> NWListener {
        while true {
            let tcp = NWProtocolTCP.Options()
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 15
            tcp.keepaliveCount = 4
            tcp.keepaliveInterval = 5
            let params = NWParameters(tls: nil, tcp: tcp)
            params.allowLocalEndpointReuse = true
            let candidate: NWListener
            do {
                candidate = try NWListener(
                    using: params, on: NWEndpoint.Port(rawValue: config.port)!)
            } catch {
                throw FatalStartupError("could not open tcp/\(config.port): \(error)")
            }
            candidate.service = advertiseService
            candidate.newConnectionHandler = { [weak self] conn in
                guard let self else { conn.cancel(); return }
                Task { await self.handleInbound(conn) }
            }
            self.listener = candidate
            do {
                try await withCheckedThrowingContinuation {
                    (cont: CheckedContinuation<Void, Error>) in
                    let resumed = Locked(false)
                    candidate.stateUpdateHandler = { state in
                        switch state {
                        case .ready:
                            if !resumed.exchange(true) { cont.resume() }
                        case .failed(let error):
                            if !resumed.exchange(true) {
                                if case .posix(let code) = error, code == .EADDRINUSE {
                                    cont.resume(throwing: PortInUseError())
                                } else {
                                    cont.resume(throwing: error)
                                }
                            }
                        case .cancelled:
                            if !resumed.exchange(true) {
                                cont.resume(throwing: WireConnectionError.cancelled)
                            }
                        default:
                            break
                        }
                    }
                    candidate.start(queue: .global(qos: .userInitiated))
                }
                return candidate
            } catch is PortInUseError {
                candidate.cancel()
                self.listener = nil
                attempt += 1
                guard attempt <= 4 else {
                    throw FatalStartupError(
                        "port \(config.port) still in use after cleanup attempt; "
                        + "another process may have grabbed it")
                }
                AnyLog.shared.info(
                    "tcp/\(config.port) still in use; retrying bind (\(attempt)/4)")
                try await Task.sleep(nanoseconds: 500_000_000)
            }
        }
    }

    private func handleInbound(_ conn: NWConnection) async {
        let framed = FramedConnection(connection: conn)
        do { try await framed.start() } catch { framed.cancel(); return }
        AnyLog.shared.debug("inbound from \(framed.remoteIP ?? "?")")
        if let ip = framed.remoteIP, authGate.isBlocked(ip) {
            AnyLog.shared.info(
                "auth gate: \(ip) blocked (>\(AuthGate.maxFails) failures, "
                + "cooldown \(Int(AuthGate.cooldown))s)")
            framed.cancel()
            return
        }
        _ = await handshakeAndRoute(framed, inbound: true)
        // On success the routed PeerLink owns `framed`; every refusal path in
        // handshakeAndRoute already cancelled it. Do NOT cancel here.
    }

    // ---- outbound dial -------------------------------------------------
    public func tryConnect(to endpoint: NWEndpoint, label: String) async -> ConnectOutcome {
        if connecting.contains(label) {
            AnyLog.shared.debug("connect to \(label) already in flight, skipping")
            return .busy
        }
        if links.count >= maxPeers { return .atCap }
        connecting.insert(label)
        defer { connecting.remove(label) }
        let framed = FramedConnection.outbound(to: endpoint)
        do {
            try await withTimeout(seconds: Wire.connectTimeout) { try await framed.start() }
        } catch {
            AnyLog.shared.info("connect to \(label) failed: \(error)")
            framed.cancel()
            return .failed
        }
        AnyLog.shared.debug("outbound connected to \(label)")
        let routed = await handshakeAndRoute(framed, inbound: false)
        return routed ? .routed : .failed
    }

    // ---- gate + routing ------------------------------------------------
    /// Exchange hellos, run the full pre-routing gate (IP block/record, token,
    /// version negotiation w/ major refusal, self/loopback drop), then route.
    /// Returns true when the handshake succeeds (link created/replaced or
    /// tie-broken); false on any refusal (framed cancelled on every false path).
    private func handshakeAndRoute(_ framed: FramedConnection, inbound: Bool) async -> Bool {
        let myHello = WireMessage.hello(
            tokenHash: tokenHash, nodeID: nodeID,
            name: config.name, appVersion: config.appVersion)
        do { try await framed.sendFrame(myHello) } catch { framed.cancel(); return false }
        let addr = framed.remoteIP ?? ""

        let peerHello: WireMessage?
        do {
            peerHello = try await withTimeout(seconds: Wire.handshakeTimeout) {
                try await framed.receiveMessage()
            }
        } catch is TimeoutError {
            AnyLog.shared.warning("handshake timeout")
            emit?(.handshakeFailed(addr: addr, reason: "timeout"))
            framed.cancel(); return false
        } catch {
            framed.cancel(); return false
        }
        guard let hello = peerHello, hello.type == "hello" else {
            AnyLog.shared.warning("invalid hello, closing")
            emit?(.handshakeFailed(addr: addr, reason: "invalid"))
            framed.cancel(); return false
        }
        let peerIP = inbound ? framed.remoteIP : nil
        guard hello.token == tokenHash else {
            AnyLog.shared.warning("auth failed from peer name=\(hello.name ?? "?")")
            if let ip = peerIP { authGate.recordFail(ip) }
            emit?(.handshakeFailed(addr: peerIP ?? addr, reason: "auth"))
            framed.cancel(); return false
        }
        let peerVersion = hello.peerVersionInfo()
        let localVersion = VersionInfo(
            appVersion: config.appVersion,
            protocolMajor: Wire.protocolMajor, protocolMinor: Wire.protocolMinor)
        let compat = negotiate(local: localVersion, peer: peerVersion)
        guard linkAllowed(compat) else {
            AnyLog.shared.warning(
                "version refused: local proto=\(Wire.protocolMajor).\(Wire.protocolMinor) "
                + "vs peer proto=\(peerVersion.protocolMajor).\(peerVersion.protocolMinor) "
                + "app=\(peerVersion.appVersion) -> \(compat.rawValue)")
            emit?(.handshakeFailed(addr: addr, reason: "version:\(compat.rawValue)"))
            framed.cancel(); return false
        }
        if compat != .compatible {
            AnyLog.shared.info("version mismatch (link kept): \(compat.rawValue)")
        }
        guard let peerID = hello.node_id, peerID != nodeID else {
            AnyLog.shared.debug("self loopback or bad node_id, dropping")
            framed.cancel(); return false
        }
        if let ip = peerIP { authGate.recordOK(ip) }
        let display = (hello.name?.isEmpty == false) ? hello.name! : String(peerID.prefix(8))

        route(framed: framed, peerID: peerID, name: display,
              peerMinor: peerVersion.protocolMinor, inbound: inbound,
              appVersion: peerVersion.appVersion)
        return true
    }

    /// Registration / tie-break / cap. Synchronous & await-free = atomic.
    private func route(
        framed: FramedConnection, peerID: String, name: String,
        peerMinor: Int, inbound: Bool, appVersion: String
    ) {
        if let existing = links[peerID] {
            let race = (monotonicNow() - existing.linkedAt) < Wire.raceWindow
            if race {
                // Genuine simultaneous-connect: lexicographic node_id tie-break.
                let keepThisLink = (!inbound && nodeID < peerID) || (inbound && nodeID > peerID)
                if !keepThisLink {
                    AnyLog.shared.debug("tie-breaker: dropping duplicate link (race)")
                    framed.cancel()
                    return
                }
                AnyLog.shared.debug("tie-breaker: replacing existing link (race)")
            } else {
                // Established link: a newcomer means the peer thinks ours is dead.
                AnyLog.shared.info("replacing link with \(name) (peer reconnected)")
            }
            // Overwrite (below) BEFORE the old task's linkClosed can run (we are
            // in a no-await critical section), so the old link's teardown sees a
            // mismatched gen and does not remove/emit for the replacement.
            existing.task.cancel()
            existing.link.close()
            registerLink(framed: framed, peerID: peerID, name: name,
                         peerMinor: peerMinor, inbound: inbound, appVersion: appVersion)
            return
        }
        // New node_id: cap applies here only (a known reconnect is routed above).
        if links.count >= maxPeers {
            AnyLog.shared.info("peer cap reached (\(maxPeers)); refusing \(name)")
            framed.cancel()
            return
        }
        registerLink(framed: framed, peerID: peerID, name: name,
                     peerMinor: peerMinor, inbound: inbound, appVersion: appVersion)
    }

    private func registerLink(
        framed: FramedConnection, peerID: String, name: String,
        peerMinor: Int, inbound: Bool, appVersion: String
    ) {
        linkGen += 1
        let gen = linkGen
        let deliver = onClip ?? { _, _ in }
        let interval = pingInterval
        let deadFactor = pingDeadFactor
        let link = PeerLink(
            conn: framed, peerNodeID: peerID, peerName: name,
            peerProtocolMinor: peerMinor, onClip: deliver)
        // Per-link tasks: the receive loop + this link's OWN staleness dropper.
        // One sleeping peer drops only its link (spec: per-link watchdog).
        let task = Task { [weak self] in
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await link.run() }
                group.addTask {
                    try? await linkPingLoop(link: link, interval: interval, deadFactor: deadFactor)
                }
                _ = await group.next()   // run() returned (EOF / close)
                group.cancelAll()
            }
            await self?.linkClosed(peerID: peerID, gen: gen, reason: "peer disconnected")
        }
        links[peerID] = LinkEntry(link: link, task: task, linkedAt: monotonicNow(), gen: gen)
        AnyLog.shared.info(
            "linked with peer name=\(name) id=\(peerID.prefix(8)) "
            + "(\(inbound ? "inbound" : "outbound")) peer_app_version=\(appVersion) "
            + "peer_proto=\(Wire.protocolMajor).\(peerMinor) [links=\(links.count)]")
        emit?(.linkUp(nodeID: peerID, peerName: name))
    }

    /// Dead link -> remove from the table immediately (frees a cap slot) and
    /// emit linkDown. Guarded by gen so a replaced link's late teardown no-ops.
    private func linkClosed(peerID: String, gen: Int, reason: String) {
        guard let entry = links[peerID], entry.gen == gen else { return }
        links.removeValue(forKey: peerID)
        AnyLog.shared.info("peer disconnected: \(peerID.prefix(8)) [links=\(links.count)]")
        emit?(.linkDown(nodeID: peerID, reason: reason))
    }

    // ---- broadcast -----------------------------------------------------
    /// Fan a local clip out to every active link. Per-link protocol-minor
    /// downgrade is evaluated per link; a per-link send failure drops ONLY that
    /// link. Echo-suppression (shouldSend) is the caller's job — evaluated once.
    public func broadcast(_ payload: ClipPayload) async -> BroadcastResult {
        var delivered: [(peerName: String, payload: ClipPayload)] = []
        var maxDropped = 0
        for entry in links.values {
            let link = entry.link
            let (maybe, dropped) = downgradeForPeer(payload, peerMinor: link.peerProtocolMinor)
            guard let outPayload = maybe else { continue }
            maxDropped = max(maxDropped, dropped)
            let ok = await link.sendClip(outPayload)
            if !ok {
                AnyLog.shared.info("send failed to \(link.peerName); dropping link")
                link.close()   // wakes run(); its task removes the entry + emits linkDown
                continue
            }
            delivered.append((peerName: link.peerName, payload: outPayload))
        }
        return BroadcastResult(delivered: delivered, maxDropped: maxDropped)
    }

    // ---- shutdown ------------------------------------------------------
    public func shutdown() {
        for entry in links.values {
            entry.task.cancel()
            entry.link.close()
        }
        links.removeAll()
        listener?.cancel()
        listener = nil
        isServing = false
    }
}
```

- [ ] **Step 3c: Re-key `Watchdogs.swift`** — `linkPingLoop` (lines 27-38) and `networkWatchdog` (lines 42-52) are UNCHANGED (`linkPingLoop` is now spawned per-link inside `LinkManager.registerLink`). Replace `idleLinkWatchdog` (lines 59-83) with:
```swift
/// Self-heal mDNS when NO link is active for too long: refresh browse +
/// re-announce up to `refreshAttempts` times, then bounce the daemon. Keys on
/// the manager's active-link count, never on a single link's idleness — keyed
/// per-link, one sleeping peer would bounce the daemon and tear down every
/// healthy link (spec: global escalator fires only at zero active links).
public func idleLinkWatchdog(
    beacon: MdnsBeacon, manager: LinkManager,
    idleThreshold: Double = 60, refreshAttempts: Int = 3
) async throws {
    var consecutiveIdle = 0
    while true {
        try await sleepSeconds(idleThreshold)
        if await manager.activeLinkCount() > 0 {
            consecutiveIdle = 0
            continue
        }
        consecutiveIdle += 1
        if consecutiveIdle <= refreshAttempts {
            AnyLog.shared.info(
                "no active links for \(Int(idleThreshold * Double(consecutiveIdle)))s; "
                + "refreshing mDNS (attempt \(consecutiveIdle)/\(refreshAttempts))")
            await beacon.refresh()
            await manager.reAnnounce()
        } else {
            throw DaemonRestartError(
                "no active links after \(refreshAttempts) mDNS refresh attempts; "
                + "bouncing daemon")
        }
    }
}
```
Then replace `mdnsReconnectLoop` (lines 87-134) with the mesh version:
```swift
/// Ensure a link to every discovered peer (up to the cap). Keyed per address on
/// the beacon; the manager's node_id table de-dupes so an already-meshed peer is
/// never re-dialed. Re-admission after a drop happens here on the next pass (no
/// new timer). Backoff is per-address via recordFail/pruneAddress.
public func mdnsReconnectLoop(beacon: MdnsBeacon, manager: LinkManager) async throws {
    while true {
        let peers = await beacon.peersSnapshot()
        for peer in peers {
            if await manager.atCap { break }               // no free slots this pass
            if await manager.hasLink(nodeID: peer.peerID) { continue }  // already meshed
            let outcome = await manager.tryConnect(to: peer.endpoint, label: peer.label)
            switch outcome {
            case .routed:
                await beacon.clearFails(label: peer.label)
            case .atCap, .busy:
                break                                       // don't penalise the address
            case .failed:
                let fails = await beacon.recordFail(label: peer.label)
                if fails >= Wire.maxReconnectFails {
                    await beacon.pruneAddress(label: peer.label)
                    AnyLog.shared.info(
                        "pruned stale peer address \(peer.label) after \(fails) failed "
                        + "attempts; awaiting fresh mDNS discovery")
                }
            }
        }
        try await sleepSeconds(2)
    }
}
```

- [ ] **Step 3d: Extend `MdnsBeacon.swift`** — carry the peer node_id so callers can de-dupe already-meshed peers. Three edits.

  Edit 1 — `onPeer` property + `init` param type (lines 12 and 26). Change the stored property declaration:
  - Old: `    private let onPeer: @Sendable (NWEndpoint, String) async -> Void`
  - New: `    private let onPeer: @Sendable (NWEndpoint, String, String) async -> Void`

  and the init parameter:
  - Old: `        onPeer: @escaping @Sendable (NWEndpoint, String) async -> Void`
  - New: `        onPeer: @escaping @Sendable (NWEndpoint, String, String) async -> Void`

  Edit 2 — `ingest` call (line 74):
  - Old: `        await onPeer(endpoint, label)`
  - New: `        await onPeer(endpoint, label, peerID)`

  Edit 3 — `peersSnapshot` (lines 99-107). Replace the whole method:
```swift
    /// Known peers deduped by address label (a restarted remote daemon leaves
    /// several stale node ids behind for the same address). Carries the node_id
    /// so the reconnect loop skips peers already in the manager's link table.
    public func peersSnapshot() -> [(endpoint: NWEndpoint, label: String, peerID: String)] {
        var seen = Set<String>()
        var out: [(endpoint: NWEndpoint, label: String, peerID: String)] = []
        for (peerID, value) in knownPeers where !seen.contains(value.label) {
            seen.insert(value.label)
            out.append((value.endpoint, value.label, peerID))
        }
        return out
    }
```

- [ ] **Step 3e: Update `MdnsBeaconTests.swift`** — `makeBeacon` (line 8), match the new onPeer arity:
  - Old: `    MdnsBeacon(nodeID: nodeID, emit: { _ in }, onPeer: { _, _ in })`
  - New: `    MdnsBeacon(nodeID: nodeID, emit: { _ in }, onPeer: { _, _, _ in })`

  (The `peersSnapshot` assertions read `peers[0].label` / `peers.count` and stay valid with the 3-tuple.)

- [ ] **Step 3f: Rewire `Daemon.swift`** — replace everything from `let link = PeerLink(` (line 131) through the end of the `cleanup` method (line 314) with:
```swift
        let manager = LinkManager(
            config: LinkManager.LinkConfig(
                token: config.token, port: config.port,
                name: config.name, appVersion: appVersion),
            nodeID: nodeID)

        // Holder breaks the watcher <-> apply callback cycle.
        let watcherBox = Locked<ClipboardWatcher?>(nil)

        // Serialized inbound applies: every link delivers here; a single drain
        // task applies them in FIFO order so markReceived + the clipboard write
        // stay ordered across ALL peers. That ordering (apply order == mark
        // order) is what keeps the single-slot-per-kind suppressor sufficient
        // under N peers.
        let (inboundStream, inboundCont) =
            AsyncStream.makeStream(of: (ClipPayload, String).self)

        let applyClip: @Sendable (ClipPayload, String) async -> Void = { payload, peer in
            // Mark BEFORE writing local clipboard so the outbound poller sees the
            // suppression flag in time.
            await coordinator.markReceived(kind: payload.kind, hash: payload.payloadHash)
            switch payload {
            case .text(let text):
                await MainActor.run { watcherBox.get()?.updateLocalText(text) }
                AnyLog.shared.info("<- received text \(text.count) chars from \(peer)")
                notify("AnyClip ← \(peer)", preview(text))
            case .image(let png):
                let ok = await MainActor.run {
                    watcherBox.get()?.updateLocalImage(png) ?? false
                }
                AnyLog.shared.info(
                    "<- received image \(png.count) bytes from \(peer) "
                    + "(\(ok ? "written to clipboard" : "WRITE FAILED"))")
                notify("AnyClip ← \(peer)", "image (\(png.count / 1024) KB)")
            case .file(let name, let data):
                let ok = await MainActor.run {
                    watcherBox.get()?.updateLocalFile(name: name, data: data) ?? false
                }
                AnyLog.shared.info(
                    "<- received file \(name) \(data.count) bytes from \(peer) "
                    + "(\(ok ? "written to clipboard" : "WRITE FAILED"))")
                notify("AnyClip ← \(peer)", "file: \(name) (\(data.count / 1024) KB)")
            case .files(let fs):
                let placed = await MainActor.run {
                    watcherBox.get()?.updateLocalFiles(fs) ?? []
                }
                // If exactly one file landed the watcher re-detects it as a
                // single-file copy (kind "file"), so also suppress that hash.
                if placed.count == 1 {
                    await coordinator.markReceived(
                        kind: "file", hash: sha256Hex(placed[0].data))
                }
                AnyLog.shared.info(
                    "<- received \(fs.count) files from \(peer) "
                    + "(\(placed.count) written to clipboard)")
                notify("AnyClip ← \(peer)", "\(placed.count) files")
            }
        }

        await manager.setHandlers(
            onClip: { payload, peer in inboundCont.yield((payload, peer)) },
            emit: emit)

        // Outbound sends run on their OWN task, fed by a non-blocking queue, so
        // the clipboard poll loop can NEVER be frozen by a stalled send.
        let outbound = OutboundQueue()

        let sendOutbound: @Sendable (ClipPayload) async -> Void = { rawPayload in
            // Global echo-suppression, evaluated ONCE per local copy: a
            // just-received clip must not be rebroadcast to any peer. This is
            // what makes the full mesh relay-free.
            guard await coordinator.shouldSend(
                kind: rawPayload.kind, hash: rawPayload.payloadHash)
            else {
                AnyLog.shared.debug("skip echo of just-received \(rawPayload.kind)")
                return
            }
            let result = await manager.broadcast(rawPayload)
            for d in result.delivered {
                switch d.payload {
                case .text(let text):
                    AnyLog.shared.info("-> sent text \(text.count) chars to \(d.peerName)")
                    notify("AnyClip → \(d.peerName)", preview(text))
                case .image(let png):
                    AnyLog.shared.info("-> sent image \(png.count) bytes to \(d.peerName)")
                    notify("AnyClip → \(d.peerName)", "image (\(png.count / 1024) KB)")
                case .file(let name, let data):
                    AnyLog.shared.info("-> sent file \(name) \(data.count) bytes to \(d.peerName)")
                    notify("AnyClip → \(d.peerName)", "file: \(name) (\(data.count / 1024) KB)")
                case .files(let fs):
                    let total = fs.reduce(0) { $0 + $1.data.count }
                    AnyLog.shared.info("-> sent \(fs.count) files \(total) bytes to \(d.peerName)")
                    notify("AnyClip → \(d.peerName)", "\(fs.count) files")
                }
            }
            // Old-peer fallback aggregated into ONE toast across all peers (same
            // principle as the folder-skip aggregation, commit d8894a0).
            if result.maxDropped > 0 {
                notify("AnyClip",
                    "\(result.maxDropped) file(s) not synced — update the peer to receive multiple files")
            }
        }

        let pollInterval = config.pollInterval
        let watcher = await MainActor.run {
            ClipboardWatcher(
                pollInterval: pollInterval, receivedDir: receivedDir,
                callbacks: ClipboardWatcher.Callbacks(
                    onChange: { payload in outbound.enqueue(payload) },
                    onFileSkipped: { message in notify("AnyClip", message) }))
        }
        watcherBox.set(watcher)

        let beacon = MdnsBeacon(
            nodeID: nodeID, emit: emit,
            onPeer: { [weak manager] endpoint, label, peerID in
                guard let manager else { return }
                // Skip peers we already mesh with (keyed by the mDNS TXT id) so a
                // re-discovery never triggers a spurious reconnect/replacement.
                if await manager.hasLink(nodeID: peerID) { return }
                _ = await manager.tryConnect(to: endpoint, label: label)
            })

        let txtData = TXTCodec.encode([
            ("id", nodeID),
            ("version", "\(Wire.legacyVersion)"),
            ("app_version", appVersion),
            ("protocol_major", "\(Wire.protocolMajor)"),
            ("protocol_minor", "\(Wire.protocolMinor)"),
        ])
        await manager.configureAdvertising(
            instanceName: "\(config.name)-\(nodeID.prefix(8))", txtData: txtData)
        await beacon.start()
        AnyLog.shared.info(
            "AnyClip starting (node \(nodeID.prefix(8)), name=\(config.name))")

        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await manager.serve() }
                group.addTask { try await watcher.run() }
                group.addTask { await outbound.run(send: sendOutbound) }
                // Serialized inbound applies (ONE drain across all links).
                group.addTask {
                    for await (payload, peer) in inboundStream {
                        if Task.isCancelled { break }
                        await applyClip(payload, peer)
                    }
                }
                group.addTask { try await mdnsReconnectLoop(beacon: beacon, manager: manager) }
                group.addTask { try await networkWatchdog(beacon: beacon) }
                group.addTask { try await idleLinkWatchdog(beacon: beacon, manager: manager) }
                group.addTask { [emit] in
                    let result = try await runProbe(
                        eventsSeen: { await beacon.eventsSeen },
                        hasNetwork: { primaryIPv4() != nil })
                    switch result {
                    case .blockedLocalNetwork:
                        AnyLog.shared.warning(
                            "permission probe: no mDNS activity in 30s -- "
                            + "Local Network permission likely blocked")
                        emit(.permissionMissing(kind: "local_network"))
                    case .noNetwork:
                        AnyLog.shared.warning("permission probe: no active network interface")
                        emit(.permissionMissing(kind: "no_network"))
                    case .ok:
                        AnyLog.shared.debug("permission probe: ok")
                    }
                }
                for try await _ in group {}
            }
        } catch {
            inboundCont.finish()
            await cleanup(manager: manager, beacon: beacon, receivedDir: receivedDir)
            throw error
        }
        inboundCont.finish()
        await cleanup(manager: manager, beacon: beacon, receivedDir: receivedDir)
    }

    private func cleanup(manager: LinkManager, beacon: MdnsBeacon, receivedDir: URL) async {
        await manager.shutdown()
        await beacon.stop()
        PidLock.release(dir: stateDir)
        clearDirectoryFiles(receivedDir)
    }
}
```
(The per-link `linkPingLoop` is no longer in the Daemon task group — `LinkManager.registerLink` spawns one per link.)

- [ ] **Step 3g: Delete the superseded PeerLink test file**
```
git rm formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift
```
(All its coverage — handshake, wrong token, ping/pong, stale-link, major mismatch, bind retry, multi-file exchange, peer-minor — is reproduced against the manager in `LinkManagerTests.swift`.)

- [ ] **Step 4: Run tests to verify they pass** — `swift test --package-path formacOS`
  Expected: whole suite green. `LinkManagerTests` (13 tests) pass; `MdnsBeaconTests`, `DaemonTests` (incl. `downgradeForPeer*`, `daemonStartsAndShutsDownCleanly`), `InteropTests` (existing single-peer), `GoldenVectorTests` all still pass (no wire change). If `staleSilentLinkIsDropped` or `idleWatchdog…` flake on a loaded machine, re-run the filtered suite `--filter LinkManagerTests`.

- [ ] **Step 5: Commit**
```
git add formacOS/Sources/AnyClipDaemon/LinkManager.swift formacOS/Sources/AnyClipDaemon/PeerLink.swift formacOS/Sources/AnyClipDaemon/Watchdogs.swift formacOS/Sources/AnyClipDaemon/MdnsBeacon.swift formacOS/Sources/AnyClipDaemon/Daemon.swift formacOS/Tests/AnyClipDaemonTests/LinkManagerTests.swift formacOS/Tests/AnyClipDaemonTests/MdnsBeaconTests.swift
git rm formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift
git commit -m "$(cat <<'EOF'
feat(daemon): LinkManager full-mesh routing, broadcast, serialized applies

Split PeerLink into LinkManager (listener, node_id-keyed link table,
pre-routing gate, cap 8, broadcast fan-out, tie-break/replace/re-admit) +
a narrowed per-session PeerLink. Local clips broadcast to every link with
per-link minor downgrade + aggregated old-peer toast; received clips apply
through one serial queue (no relay). Watchdogs re-keyed: per-link staleness
dropper per link, mDNS escalator keyed on zero active links. No wire change.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 6: Render multi-peer status + two-peer non-relay interop test

**Files:**
- Modify: `formacOS/Sources/AnyClipApp/StatusItemController.swift` — line 119 (linked status render)
- Test: `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift` (append one test + two private helpers)

**Interfaces:**
- Consumes: `PeerUIState.sortedPeerNames` (Task 4); `LinkManager`, `LinkManager.LinkConfig`, `LinkManager.broadcast`, `activeLinkCount`, `tryConnect`, `shutdown` (Task 5); existing `scriptsDir()` in `InteropTests.swift`; `fake_peer.py` (unchanged, stdlib peer that listens, handshakes, auto-sends one text clip, records received frames).
- Produces: none consumed downstream (leaf task).

- [ ] **Step 1: Write the failing test** — append to `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift`:
```swift
/// Spawn a wire-compatible fake peer that listens on `port`, handshakes, and
/// auto-sends one text clip; returns the process (for teardown) and its
/// record outfile. Waits for READY on stdout.
private func startFakePeer(port: UInt16, token: String) async throws -> (Process, URL) {
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", token, "--out", outFile.path,
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    var ready = false
    let deadline = Date().addingTimeInterval(10)
    var accumulated = Data()
    while Date() < deadline {
        let chunk = stdout.fileHandleForReading.availableData
        if !chunk.isEmpty { accumulated.append(chunk) }
        if let s = String(data: accumulated, encoding: .utf8), s.contains("READY") {
            ready = true; break
        }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    guard ready else { throw WireConnectionError.closed }
    return (process, outFile)
}

/// Count of frames the fake peer RECEIVED from us (its "recv" event lines).
private func recvClipCount(_ url: URL) -> Int {
    guard let s = try? String(contentsOf: url, encoding: .utf8) else { return 0 }
    return s.split(separator: "\n").filter { $0.contains("\"event\": \"recv\"") }.count
}

private func fileContains(_ url: URL, _ needle: String) -> Bool {
    (try? String(contentsOf: url, encoding: .utf8))?.contains(needle) ?? false
}

@Test func meshLinksBothPeersAndNeverRelays() async throws {
    let portA: UInt16 = 28495
    let portB: UInt16 = 28496
    let (procA, outA) = try await startFakePeer(port: portA, token: "mesh-token")
    let (procB, outB) = try await startFakePeer(port: portB, token: "mesh-token")
    defer {
        if procA.isRunning { procA.terminate() }
        if procB.isRunning { procB.terminate() }
    }

    let clips = Locked<[(ClipPayload, String)]>([])
    let manager = LinkManager(
        config: LinkManager.LinkConfig(
            token: "mesh-token", port: 28497, name: "swift-mesh", appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await manager.setHandlers(
        onClip: { payload, peer in clips.set(clips.get() + [(payload, peer)]) },
        emit: { _ in })

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    // Dial BOTH peers: full mesh from this node's side.
    _ = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: portA)!), label: "a")
    _ = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: portB)!), label: "b")
    #expect(await waitUntil(5) { await manager.activeLinkCount() == 2 })

    // Both fake peers auto-send "hello-from-python"; the manager APPLIES both
    // locally (onClip records them). This proves both links carry traffic.
    #expect(await waitUntil(5) {
        clips.get().filter {
            if case .text(let s) = $0.0 { return s == "hello-from-python" }; return false
        }.count == 2
    })

    // Non-relay: the manager received A's (and B's) clip but has NO relay path,
    // so it must forward nothing to the other peer. Give it a bounded window,
    // then assert neither fake peer received ANY clip frame from us. (This is
    // also the echo-suppression-under-mesh guarantee: an applied clip is never
    // rebroadcast.)
    try await Task.sleep(nanoseconds: 1_000_000_000)
    #expect(recvClipCount(outA) == 0)
    #expect(recvClipCount(outB) == 0)

    // The mesh works the other way: one LOCAL clip reaches BOTH peers.
    _ = await manager.broadcast(.text("local-broadcast"))
    #expect(await waitUntil(5) {
        fileContains(outA, "local-broadcast") && fileContains(outB, "local-broadcast")
    })
    // That broadcast is the ONLY frame each peer received — still no relay of
    // the other peer's hello clip.
    #expect(await waitUntil(3) { recvClipCount(outA) == 1 })
    #expect(await waitUntil(3) { recvClipCount(outB) == 1 })

    await manager.shutdown()
}
```

- [ ] **Step 2: Run test to verify it fails** — `swift test --package-path formacOS --filter meshLinksBothPeersAndNeverRelays`
  Expected: the whole package still builds (Task 5 landed), but this NEW test fails first at compile only if a helper name clashes; otherwise it passes immediately because the mesh is already wired. To make the RED step meaningful, first run it against a deliberately mis-set expectation is unnecessary — instead confirm the render change is still pending: `swift test --package-path formacOS --filter meshLinksBothPeersAndNeverRelays` should PASS here (behavior already implemented in Task 5). If it does not pass, debug the interop harness before touching the UI. (This test is an acceptance guard for the Task 5 mesh; the UI change below has no unit harness.)

- [ ] **Step 3: Render all linked peers** — edit `formacOS/Sources/AnyClipApp/StatusItemController.swift` line 119:
  - Old: `            statusMenuItem.title = "Linked: \(state.peerName ?? "peer")"`
  - New: `            statusMenuItem.title = "Linked: " + state.sortedPeerNames.joined(separator: ", ")`

  (Zero-peer states never reach `.linked`, so the join is always non-empty here;
  the `.searching`/`.idle`/`.error` arms are unchanged.)

- [ ] **Step 4: Run tests to verify they pass** — `swift test --package-path formacOS`
  Expected: whole suite green, including the two existing single-peer `InteropTests`, the new `meshLinksBothPeersAndNeverRelays`, and `GoldenVectorTests` (wire unchanged). Manually confirm the menu bar shows e.g. `Linked: android-9, win-pc` with two peers (optional; no unit harness for `StatusItemController`).

- [ ] **Step 5: Commit**
```
git add formacOS/Sources/AnyClipApp/StatusItemController.swift formacOS/Tests/AnyClipDaemonTests/InteropTests.swift
git commit -m "$(cat <<'EOF'
feat(app): render all linked peers + two-peer non-relay interop test

Status line now lists every meshed peer (ordinally sorted, comma-joined).
New interop test spawns two fake_peer.py instances: both handshake, a local
clip reaches both, and neither receives the other's clip (non-relay + echo
suppression under mesh).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

## Test-matrix coverage map (spec §Testing)

| Spec requirement | Covered by |
|---|---|
| new node_id creates a link | `newNodeCreatesLinkAndEmitsLinkUp` (Task 5) |
| known node_id routes to it / duplicate replaces live session | `duplicateNodeReplacesLiveSession` (Task 5) |
| over-cap NEW peer refused; known reconnect at cap routed | `overCapNewPeerRefusedKnownReconnectRouted` (Task 5) |
| dead link leaves table / frees cap slot | `deadLinkFreesCapSlot` (Task 5) |
| broadcast fan-out + per-link failure isolation | `broadcastFansOutAndIsolatesFailure` (Task 5) |
| per-link minor gating (files vs first-file fallback) | `perLinkMinorGatingFilesVsFallback` (Task 5) |
| global escalator fires only at zero active links | `idleWatchdogFiresOnlyAtZeroActiveLinks` (Task 5) |
| suppressor under serialized applies / non-relay | `meshLinksBothPeersAndNeverRelays` (Task 6 interop) |
| multi-peer state/reducer (add/remove by node_id) | `PeerStateTests` (Task 4) |
| golden vectors unchanged (wire unchanged) | `GoldenVectorTests` (runs every pass) |

---

# Part 3 — C# (Tasks 7–9)


**Goal:** Turn the Windows native build (`forwindows/`, shipped app version 1.3.0) from a
single active TCP link into a **full mesh** — every daemon links to every discovered
same-token peer (up to a cap) and local clips broadcast to all links. **No wire change:**
protocol stays 1.1; hello/framing/golden vectors are untouched. Spec:
`docs/superpowers/specs/2026-07-22-desktop-multipeer-design.md`.

**Architecture:** split today's `PeerLink` (which owns the TCP server, outbound connects, the
hello exchange, the pre-routing gate, the tie-break, and the single session) into:

- **`LinkManager`** (new) — owns the listening socket, the `AuthGate`, the **active-link table
  keyed by `node_id`**, and the pre-routing gate. Inbound and outbound both exchange hellos,
  run the gate (IP-block/record, token, version major-mismatch refusal, self/loopback drop)
  **before any routing**, then route: create/replace/refuse a per-peer `PeerLink`, handing it
  the open connection **and the parsed hello** so the session never re-reads a hello.
  Broadcasts fan out to every active link with per-link protocol-minor gating; received applies
  serialize through one queue.
- **`PeerLink`** (narrowed) — exactly one peer pair: the post-hello session, keepalive, per-link
  staleness watchdog, per-link send. Emits `LinkUp` at session start and `LinkDown` at teardown.

**Cross-implementation contract (FIXED — do not deviate; Python/Swift drafters use the same
shapes):** events gain `node_id`; `PeerUiState` becomes a peer collection keyed by `node_id`;
the reducer adds/removes entries; `LinkManager`/`PeerLink` split as above; cap
`DefaultMaxPeers = 8`; duplicate connection for a live `node_id` **replaces** (tie-break only
inside the race window); dead link leaves the table immediately; broadcast per-link failure
drops only that link; receive applies serialized; global escalator keys on **zero** active
links; per-link staleness stays per link; skip/fallback toast for one local copy aggregated
into **one** toast across all peers.

## Global constraints (every task)

- **No wire change.** `Wire.ProtocolMinor` stays `1`; do **not** touch `WireMessage`,
  `Wire.cs`, `GoldenVectorTests`, or the golden `.bin` fixtures. App version 1.3.0 comes from
  the release tag; **no version constant changes**.
- **Build/test (this Mac):** `AnyClipCore` + `AnyClipCore.Tests` build and run cross-platform:
  `dotnet test forwindows/tests/AnyClipCore.Tests`. `AnyClipApp` is WinForms and builds/tests
  **only on Windows** — App-side edits (TrayIcon) are verified on Windows CI, not by the macOS
  command. State this in any App step.
- **Branch:** `feat/desktop-multipeer`.
- **Commits** end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- **Ordering:** Task 7 → Task 8 → Task 9. Task 8 consumes Task 7's event shapes; Task 9 consumes Task 7's
  `PeerUiState`/reducer and Task 8's `LinkManager`/narrowed `PeerLink`.
- ⚠️ **Environment warning:** running the native test suites on this Mac can flip the live
  AnyClip app into a sticky false auth-error state (see MEMORY). Known local artifact — restart
  the app to clear; it is not a test failure.

---

### Task 7: PeerState event model + peer-collection reducer

Rename the event identity field to `NodeId`, give `LinkDown` a `NodeId`, replace the single
`PeerUiState.PeerName` scalar with a `node_id → display-name` dictionary, and rewrite the
reducer to add/remove entries. Update every test that constructs these events, plus the two
`PeerLink` emit sites and the one tray call site so the tree still compiles.

**Files:**
- Modify: `forwindows/src/AnyClipCore/PeerState.cs` (records + `PeerUiState` + reducer)
- Modify: `forwindows/src/AnyClipCore/PeerLink.cs` (emit sites at lines 314, 344-362 — minimal,
  full restructure is Task 8)
- Modify: `forwindows/src/AnyClipApp/TrayIcon.cs` (line 132 — minimal compile fix; full peer-list
  rendering is Task 9; **App is Windows-only, not built by the macOS test command**)
- Test: `forwindows/tests/AnyClipCore.Tests/PeerStateTests.cs` (rewrite every event construction)

**Interfaces:**
- Consumes: existing `DaemonEvent`, `PeerStateKind`, `TrayIconSpec`, `PeerStateReducer.HandshakeFailThreshold`.
- Produces:
  - `record LinkUp(string NodeId, string PeerName) : DaemonEvent`
  - `record LinkDown(string NodeId, string Reason) : DaemonEvent`
  - `record PeerUiState(PeerStateKind Kind, IReadOnlyDictionary<string,string> Peers, double? Since = null, string? Reason = null, int ConsecutiveHandshakeFails = 0)` with `PeerUiState.Initial`
  - `PeerStateReducer.Reduce(PeerUiState prev, DaemonEvent ev, double now) : PeerUiState` — `LinkUp` inserts/updates by `NodeId`; `LinkDown` removes only that `NodeId`; `Kind` is `Linked` iff `Peers` non-empty

- [ ] **Step 1: Write the failing tests** — replace `forwindows/tests/AnyClipCore.Tests/PeerStateTests.cs` entirely:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStateTests
{
    [Fact] public void InitialIsIdleWithNoPeers()
    {
        Assert.Equal(PeerStateKind.Idle, PeerUiState.Initial.Kind);
        Assert.Empty(PeerUiState.Initial.Peers);
    }

    [Fact]
    public void LinkUpAddsPeerKeyedByNodeIdAndGoesLinked()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial,
            new LinkUp("node-abc", "win-pc"), 42.0);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal("win-pc", s.Peers["node-abc"]);
        Assert.Single(s.Peers);
        Assert.Equal(42.0, s.Since);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void SecondLinkUpAddsSecondPeerAndKeepsSince()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("b", "win"), 9);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(2, s.Peers.Count);
        Assert.Equal(1, s.Since); // first-link timestamp retained
    }

    [Fact]
    public void LinkDownRemovesOnlyThatNodeId()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("b", "win"), 2);
        s = PeerStateReducer.Reduce(s, new LinkDown("a", "peer disconnected"), 3);
        Assert.Equal(PeerStateKind.Linked, s.Kind); // still one peer
        Assert.False(s.Peers.ContainsKey("a"));
        Assert.True(s.Peers.ContainsKey("b"));
    }

    [Fact]
    public void LastLinkDownGoesSearchingWithReason()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkDown("a", "peer disconnected"), 2);
        Assert.Equal(PeerStateKind.Searching, s.Kind);
        Assert.Empty(s.Peers);
        Assert.Equal("peer disconnected", s.Reason);
    }

    [Fact]
    public void UnknownLinkDownIsANoOp()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        var s2 = PeerStateReducer.Reduce(s, new LinkDown("ghost", "x"), 2);
        Assert.Same(s.Peers, s2.Peers); // untouched -> same reference
        Assert.Equal(PeerStateKind.Linked, s2.Kind);
    }

    [Fact]
    public void DiscoveryMovesIdleAndErrorToSearchingOnlyWhenNoPeers()
    {
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1).Kind);
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(err, new PeerDiscovered("n", "a"), 2).Kind);
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "p"), 1);
        Assert.Same(linked, PeerStateReducer.Reduce(linked, new PeerDiscovered("n", "a"), 2));
    }

    [Fact]
    public void FiveHandshakeFailsTripAuthErrorFromIdle()
    {
        var s = PeerUiState.Initial;
        for (int i = 1; i < PeerStateReducer.HandshakeFailThreshold; i++)
        {
            s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), i);
            Assert.Equal(PeerStateKind.Idle, s.Kind);
            Assert.Equal(i, s.ConsecutiveHandshakeFails);
        }
        s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), 5);
        Assert.Equal(PeerStateKind.Error, s.Kind);
        Assert.Equal("auth", s.Reason);
        Assert.Equal(5, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void LinkUpResetsFailCounter()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new HandshakeFailed("a", "auth"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("a", "p"), 2);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
        s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), 3);
        Assert.NotEqual(PeerStateKind.Error, s.Kind); // still linked, one fail
        Assert.Equal(1, s.ConsecutiveHandshakeFails);
    }

    [Fact] public void ThresholdConstantIsFive() =>
        Assert.Equal(5, PeerStateReducer.HandshakeFailThreshold);

    [Fact]
    public void TrayIconSpecMapping()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "p"), 1);
        Assert.Equal(new TrayIconSpec(false, false), TrayIconSpec.For(linked));
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(PeerUiState.Initial));
        var searching = PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1);
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(searching));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(new TrayIconSpec(true, true), TrayIconSpec.For(err));
    }
}
```

- [ ] **Step 2: Run to see it fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter PeerStateTests`
  Expected: **compile error** — `LinkUp`/`LinkDown` still have the old shape and `PeerUiState`
  still exposes `PeerName`, so `new LinkUp("node-abc","win-pc")` semantics, `s.Peers[...]`, and
  `new LinkDown("a","reason")` don't match. (Xunit reports build failure for the test project.)

- [ ] **Step 3a: Rewrite `PeerState.cs`** — replace `forwindows/src/AnyClipCore/PeerState.cs` entirely:
```csharp
namespace AnyClip.Core;

public abstract record DaemonEvent;
public sealed record PeerDiscovered(string Name, string Addr) : DaemonEvent;
// Stable peer identity on both link events: node_id + display name. node_id is
// a fresh UUID per daemon start, so a peer restart arrives as a new node_id.
public sealed record LinkUp(string NodeId, string PeerName) : DaemonEvent;
public sealed record LinkDown(string NodeId, string Reason) : DaemonEvent;
public sealed record HandshakeFailed(string Addr, string Reason) : DaemonEvent;
public sealed record PermissionMissing(string Kind) : DaemonEvent;

public enum PeerStateKind { Idle, Searching, Linked, Error }

/// UI state is now a peer COLLECTION keyed by node_id -> display name (was a
/// single scalar peer_name). Linked iff Peers is non-empty. Since = first-link
/// timestamp. Port of peer_state.py multi-peer state; parity with Swift
/// PeerUIState and Python peer_state.State.
public sealed record PeerUiState(
    PeerStateKind Kind,
    IReadOnlyDictionary<string, string> Peers,
    double? Since = null,
    string? Reason = null,
    int ConsecutiveHandshakeFails = 0)
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();
    public static readonly PeerUiState Initial = new(PeerStateKind.Idle, Empty);
}

/// Pure reducer — port of peer_state.py. LinkUp inserts/updates by node_id;
/// LinkDown removes ONLY that node_id (never collapses to searching while other
/// peers remain); Kind is Linked iff Peers is non-empty.
public static class PeerStateReducer
{
    public const int HandshakeFailThreshold = 5;

    public static PeerUiState Reduce(PeerUiState prev, DaemonEvent ev, double now) => ev switch
    {
        PermissionMissing p => prev with { Kind = PeerStateKind.Error, Reason = p.Kind },
        LinkUp u => WithPeer(prev, u.NodeId, u.PeerName, now),
        LinkDown d => WithoutPeer(prev, d.NodeId, d.Reason),
        PeerDiscovered when prev.Peers.Count == 0
                         && prev.Kind is PeerStateKind.Idle or PeerStateKind.Error =>
            prev with { Kind = PeerStateKind.Searching },
        PeerDiscovered => prev,
        HandshakeFailed =>
            prev.ConsecutiveHandshakeFails + 1 >= HandshakeFailThreshold
                ? prev with { Kind = PeerStateKind.Error, Reason = "auth",
                              ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 }
                : prev with { ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 },
        _ => prev,
    };

    private static PeerUiState WithPeer(PeerUiState prev, string nodeId, string name, double now)
    {
        var peers = new Dictionary<string, string>(prev.Peers) { [nodeId] = name };
        return prev with
        {
            Kind = PeerStateKind.Linked,
            Peers = peers,
            Since = prev.Peers.Count == 0 ? now : prev.Since, // first link stamps the clock
            Reason = null,
            ConsecutiveHandshakeFails = 0,                     // a live link clears auth backoff
        };
    }

    private static PeerUiState WithoutPeer(PeerUiState prev, string nodeId, string reason)
    {
        if (!prev.Peers.ContainsKey(nodeId)) return prev; // unknown drop: no-op
        var peers = new Dictionary<string, string>(prev.Peers);
        peers.Remove(nodeId);
        return prev with
        {
            Kind = peers.Count > 0 ? PeerStateKind.Linked : PeerStateKind.Searching,
            Peers = peers,
            Reason = peers.Count > 0 ? prev.Reason : reason,
        };
    }
}

/// Tray rendering spec, parity with formacOS MenuIcon: attention (red) whenever
/// not linked; ErrorBang adds the "!" overlay.
public readonly record struct TrayIconSpec(bool Attention, bool ErrorBang)
{
    public static TrayIconSpec For(PeerUiState s) => s.Kind switch
    {
        PeerStateKind.Linked => new TrayIconSpec(false, false),
        PeerStateKind.Error => new TrayIconSpec(true, true),
        _ => new TrayIconSpec(true, false),
    };
}
```

- [ ] **Step 3b: Update the two `PeerLink` emit sites (minimal — full restructure is Task 8)** —
  Edit `forwindows/src/AnyClipCore/PeerLink.cs`.
  First, line 314. Anchor:
```csharp
            Emit?.Invoke(new LinkUp(displayName, peerId));
```
  Replace with (node_id first now):
```csharp
            Emit?.Invoke(new LinkUp(peerId, displayName));
```
  Then the teardown block. Anchor on lines 344-362:
```csharp
        finally
        {
            bool wasActive;
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                wasActive = ReferenceEquals(_activeConn, framed);
                if (wasActive)
                {
                    _activeConn = null;
                    _peerNodeId = null;
                    PeerName = null;
                    PeerProtocolMinor = 0;
                }
            }
            finally { _lock.Release(); }
            RotatingLog.Shared.Info("peer disconnected");
            if (wasActive) Emit?.Invoke(new LinkDown("peer disconnected"));
        }
```
  Replace with (capture the node_id before clearing so `LinkDown` carries identity):
```csharp
        finally
        {
            bool wasActive;
            string? goneId;
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                wasActive = ReferenceEquals(_activeConn, framed);
                goneId = _peerNodeId;
                if (wasActive)
                {
                    _activeConn = null;
                    _peerNodeId = null;
                    PeerName = null;
                    PeerProtocolMinor = 0;
                }
            }
            finally { _lock.Release(); }
            RotatingLog.Shared.Info("peer disconnected");
            if (wasActive) Emit?.Invoke(new LinkDown(goneId ?? "", "peer disconnected"));
        }
```

- [ ] **Step 3c: Minimal tray compile fix (App is Windows-only; not built by the macOS test
  command)** — Edit `forwindows/src/AnyClipApp/TrayIcon.cs`, line 132. Anchor:
```csharp
            PeerStateKind.Linked => $"Linked: {state.PeerName ?? "peer"}",
```
  Replace with (peer-dict join; Task 9 replaces this with the shared `PeerStatus.Line` helper):
```csharp
            PeerStateKind.Linked => "Linked: " + (state.Peers.Count > 0
                ? string.Join(", ", state.Peers.Values) : "peer"),
```

- [ ] **Step 4: Run to see it pass** — `dotnet test forwindows/tests/AnyClipCore.Tests`
  Expected: the whole `AnyClipCore.Tests` suite builds and passes. `PeerStateTests` (12 tests)
  green; `WireMessageTests`, `GoldenVectorTests`, `PureLogicTests`, `DaemonTests`,
  `PeerLinkTests`, `InteropTests`, `PeerDirectoryTests`, `VersionNegotiatorTests` unaffected
  (they pattern-match `is LinkUp`/`is LinkDown` without constructing them and never touch
  `PeerUiState.PeerName`).

- [ ] **Step 5: Commit**
```
git add forwindows/src/AnyClipCore/PeerState.cs forwindows/src/AnyClipCore/PeerLink.cs \
        forwindows/src/AnyClipApp/TrayIcon.cs forwindows/tests/AnyClipCore.Tests/PeerStateTests.cs
git commit -m "$(cat <<'EOF'
feat(win): peer-collection UI state + node_id on link events

LinkUp/LinkDown gain a stable node_id; PeerUiState becomes a node_id ->
display-name dictionary and the reducer adds/removes entries instead of
collapsing to searching on any LinkDown. Minimal PeerLink emit-site + tray
compile fixes; the LinkManager split and full tray rendering follow.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 8: LinkManager + narrowed PeerLink + Daemon rewiring + watchdog rekey

Split `PeerLink` into `LinkManager` (server + gate + active-link table keyed by `node_id` +
broadcast + serialized receive) and a narrowed per-peer `PeerLink` (post-hello session +
keepalive + per-link staleness + per-link send). Rewire `Daemon` to the manager; rekey the
reconnect loop and idle-escalator watchdogs off the manager's active-link count.

**Files:**
- Modify (full rewrite): `forwindows/src/AnyClipCore/PeerLink.cs`
- Create: `forwindows/src/AnyClipCore/LinkManager.cs`
- Modify: `forwindows/src/AnyClipCore/Daemon.cs` (link → manager wiring; broadcast; serialized apply)
- Modify: `forwindows/src/AnyClipCore/Watchdogs.cs` (`MdnsReconnectLoopAsync`, `IdleLinkWatchdogAsync` take `LinkManager`)
- Delete → replace: `forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs` → `LinkManagerTests.cs`
- Modify: `forwindows/tests/AnyClipCore.Tests/InteropTests.cs` (drive `LinkManager`)
- Modify: `forwindows/tests/AnyClipCore.Tests/DaemonTests.cs` (downgrade test unchanged behavior)

**Interfaces:**
- Consumes: `LinkUp(string NodeId, string PeerName)`, `LinkDown(string NodeId, string Reason)`
  (Task 7); existing `Hashing`, `WireMessage`, `WireFileEntry`, `FramedConnection`,
  `VersionNegotiator`, `Compatibility`, `AuthGate`, `Wire`, `PeerDirectory`, `ClipPayload`
  family (`TextClip`/`ImageClip`/`FileClip`/`FilesClip`), `SyncCoordinator`,
  `FatalStartupException`, `DaemonRestartException`, `Watchdogs.LinkPingLoopAsync`,
  `Watchdogs.NetworkWatchdogAsync`.
- Produces:
  - `record LinkConfig(string Token, int Port, string Name, string AppVersion)` (top-level; moved out of `PeerLink.LinkConfig`)
  - `class LinkManager`:
    - `const int DefaultMaxPeers = 8`
    - `LinkManager(LinkConfig config, string nodeId, int maxPeers = DefaultMaxPeers, double linkPingInterval = 30)`
    - `Func<ClipPayload, string, Task>? OnClip` (payload + source peer display name; applies serialized)
    - `Action<DaemonEvent>? Emit`; `volatile bool IsServing`
    - `int ActiveLinkCount`; `bool AtCap`; `IReadOnlyList<string> LinkedPeerNames`; `bool HasLinkToHost(string host)`
    - `Task ServeAsync(CancellationToken ct)`; `Task TryConnectAsync(string host, int port, string label, CancellationToken ct)`
    - `Task<BroadcastResult> BroadcastAsync(ClipPayload payload)`; `readonly record struct BroadcastResult(int Sent, int OldPeerDrops)`
    - `void Shutdown()`
  - narrowed `class PeerLink`:
    - `PeerLink(string peerNodeId, string peerName, VersionInfo peerVersion, FramedConnection conn, bool inbound, string? dialLabel)`
    - `string PeerNodeId`, `string PeerName`, `int PeerProtocolMinor`, `string? RemoteHost`, `string? DialLabel`, `double LinkedAt`, `bool IsActive`
    - `Func<ClipPayload, Task>? OnClip`, `Action<DaemonEvent>? Emit`
    - `void MarkSuperseded()`, `Task RunSessionAsync(ct)`, `Task<bool> SendClipAsync(ClipPayload)`, `Task SendPingAsync()`, `double? SecondsSinceInbound()`, `void DropStaleLink(double)`, `void Dispose()`
  - `Watchdogs.MdnsReconnectLoopAsync(PeerDirectory, LinkManager, ct)`; `Watchdogs.IdleLinkWatchdogAsync(IMdnsService, LinkManager, double, int, ct)`

- [ ] **Step 1: Write the failing tests** — delete `forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs`
  and create `forwindows/tests/AnyClipCore.Tests/LinkManagerTests.cs`:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class LinkManagerTests
{
    private static LinkManager MakeManager(
        string token, int port, string name,
        List<(ClipPayload Payload, string Peer)> clips,
        List<DaemonEvent> events,
        int maxPeers = LinkManager.DefaultMaxPeers, double ping = 30)
    {
        var m = new LinkManager(
            new LinkConfig(token, port, name, "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant(), maxPeers, ping);
        m.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
        m.Emit = e => { lock (events) events.Add(e); };
        return m;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, double timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    // A raw wire peer that completes the handshake against the manager and then
    // stays open. `minor` sets its advertised protocol_minor.
    private static async Task<FramedConnection> RawHandshake(
        int port, string token, string nodeId, string name, int minor, CancellationToken ct)
    {
        var raw = await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct);
        var hello = WireMessage.Hello(Hashing.Sha256Hex(token), nodeId, name, "0.0.0-test")
            with { ProtocolMinor = minor };
        await raw.SendFrameAsync(hello, ct);
        _ = await raw.ReceiveMessageAsync(ct); // manager's hello
        return raw;
    }

    [Fact]
    public async Task TwoManagersHandshakeAndBroadcastClips()
    {
        var aClips = new List<(ClipPayload, string)>(); var aEvents = new List<DaemonEvent>();
        var bClips = new List<(ClipPayload, string)>(); var bEvents = new List<DaemonEvent>();
        var a = MakeManager("tok", 28711, "node-a", aClips, aEvents);
        var b = MakeManager("tok", 28712, "node-b", bClips, bEvents);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        await b.TryConnectAsync("127.0.0.1", 28711, "127.0.0.1:28711", cts.Token);
        Assert.True(await WaitUntil(() => a.ActiveLinkCount == 1 && b.ActiveLinkCount == 1));
        lock (aEvents) Assert.Contains(aEvents, e => e is LinkUp u && u.PeerName == "node-b");

        var res = await b.BroadcastAsync(new TextClip("from-b"));
        Assert.Equal(1, res.Sent);
        Assert.True(await WaitUntil(() =>
        { lock (aClips) return aClips.Any(c => c.Item1 is TextClip t && t.Text == "from-b"); }));
        // Source peer name threaded through the serialized apply.
        lock (aClips) Assert.Equal("node-b", aClips.First(c => c.Item1 is TextClip).Item2);

        cts.Cancel(); a.Shutdown(); b.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task WrongTokenRejectedWithAuthEvent()
    {
        var events = new List<DaemonEvent>();
        var a = MakeManager("right", 28713, "a", new(), events);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28713, 5, cts.Token);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("wrong"), "ffffffff-bad", "b", "0.0.0-test"), cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Any(e => e is HandshakeFailed { Reason: "auth" }); }));
        Assert.Equal(0, a.ActiveLinkCount);

        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MajorMismatchRefusedWithVersionEvent()
    {
        var events = new List<DaemonEvent>();
        var a = MakeManager("tok", 28714, "a", new(), events);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28714, 5, cts.Token);
        var bad = WireMessage.Hello(Hashing.Sha256Hex("tok"), "ffffffff-v2", "future", "2.0.0")
            with { ProtocolMajor = 2 };
        await raw.SendFrameAsync(bad, cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Any(e => e is HandshakeFailed h && h.Reason.StartsWith("version:")); }));
        Assert.Equal(0, a.ActiveLinkCount);

        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServeRetriesBindWhenPortTemporarilyHeld()
    {
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 28716);
        blocker.Start();
        var a = MakeManager("t", 28716, "retry", new(), new());
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        await Task.Delay(700);
        blocker.Stop();
        Assert.True(await WaitUntil(() => a.IsServing, 5));
        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task NewNodeIdRefusedAtCapCountStable()
    {
        var events = new List<DaemonEvent>();
        var m = MakeManager("tok", 28717, "cap", new(), events, maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var raw1 = await RawHandshake(28717, "tok", "aaaa-node-1", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        // New node_id while at cap -> refused; count stays 1, no LinkUp for it.
        using var raw2 = await RawHandshake(28717, "tok", "bbbb-node-2", "peer-2", 1, cts.Token);
        await Task.Delay(400);
        Assert.Equal(1, m.ActiveLinkCount);
        lock (events) Assert.DoesNotContain(events, e => e is LinkUp u && u.NodeId == "bbbb-node-2");

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task KnownNodeIdReconnectAtCapReplacesWithoutSpuriousLinkDown()
    {
        var events = new List<DaemonEvent>();
        var m = MakeManager("tok", 28718, "dup", new(), events, maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        var raw1 = await RawHandshake(28718, "tok", "dup-node", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1
            && events.Any(e => e is LinkUp u && u.NodeId == "dup-node")));

        // Outside the race window: a fresh connection for the SAME node_id, even
        // at cap, is ROUTED (replaces the live session), not refused.
        await Task.Delay(1700);
        var raw2 = await RawHandshake(28718, "tok", "dup-node", "peer-2", 1, cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Count(e => e is LinkUp u && u.NodeId == "dup-node") >= 2; }));
        Assert.Equal(1, m.ActiveLinkCount);
        // Replaced session was superseded -> no LinkDown for that node_id.
        lock (events) Assert.DoesNotContain(events, e => e is LinkDown d && d.NodeId == "dup-node");

        raw1.Dispose(); raw2.Dispose();
        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DeadLinkFreesCapSlotForNewPeer()
    {
        var m = MakeManager("tok", 28719, "cap", new(), new(), maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        var raw1 = await RawHandshake(28719, "tok", "node-1", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));
        raw1.Dispose(); // link dies -> table entry removed immediately -> slot freed
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 0));

        using var raw2 = await RawHandshake(28719, "tok", "node-2", "peer-2", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task BroadcastDowngradesFilesForOldPeerAndSendsFilesForNew()
    {
        var m = MakeManager("tok", 28720, "bcast", new(), new());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var rawNew = await RawHandshake(28720, "tok", "new-node", "new", 1, cts.Token);
        using var rawOld = await RawHandshake(28720, "tok", "old-node", "old", 0, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<(string, byte[])>
        {
            ("a.txt", "one"u8.ToArray()),
            ("b.txt", "two"u8.ToArray()),
        }));
        Assert.Equal(2, res.Sent);
        Assert.Equal(1, res.OldPeerDrops); // 2 files -> 1 dropped for the minor-0 peer

        var fNew = await rawNew.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", fNew!.Kind);
        Assert.Equal(2, fNew.Files!.Count);
        var fOld = await rawOld.ReceiveMessageAsync(cts.Token);
        Assert.Equal("file", fOld!.Kind);           // downgraded to first file
        Assert.Equal("a.txt", fOld.Name);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task FilesClipInvalidOrEmptyFrameIgnoredAndLinkStaysUp()
    {
        var clips = new List<(ClipPayload, string)>();
        var m = MakeManager("tok", 28721, "a", clips, new());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var raw = await RawHandshake(28721, "tok", "ffffffff-raw", "raw", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        await raw.SendFrameAsync(new WireMessage
        { Type = "clip", Kind = "files", Files = new List<WireFileEntry>(), Hash = "x", Ts = 1 }, cts.Token);
        await raw.SendFrameAsync(new WireMessage
        {
            Type = "clip", Kind = "files",
            Files = new List<WireFileEntry>
            {
                new() { Name = "ok.txt", Content = Convert.ToBase64String("ok"u8.ToArray()), Hash = "x", Bytes = 2 },
                new() { Name = "bad.txt", Content = "!!!not-base64!!!", Hash = "x", Bytes = 0 },
            },
            Hash = "x", Ts = 1,
        }, cts.Token);
        await raw.SendFrameAsync(WireMessage.ClipFiles(
            new List<(string, byte[])> { ("a.txt", "aa"u8.ToArray()), ("b.txt", "bb"u8.ToArray()) },
            1), cts.Token);

        Assert.True(await WaitUntil(() =>
        { lock (clips) return clips.Any(c => c.Item1 is FilesClip f && f.Files.Count == 2); }));
        lock (clips) Assert.DoesNotContain(clips, c => c.Item1 is FilesClip f && f.Files.Count != 2);
        Assert.Equal(1, m.ActiveLinkCount);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }
}
```

- [ ] **Step 2: Run to see it fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter LinkManagerTests`
  Expected: **compile failure** — `LinkManager`, `LinkConfig`, `BroadcastResult`, and the new
  `PeerLink` constructor do not exist yet. (The test project fails to build.)

- [ ] **Step 3a: Narrow `PeerLink.cs`** — replace `forwindows/src/AnyClipCore/PeerLink.cs` entirely.
  All server/connect/hello/gate/tie-break code moves to `LinkManager` (Step 3b); what remains is
  one established link's session, keepalive, per-link staleness, and per-link send:
```csharp
using System.Diagnostics;

namespace AnyClip.Core;

/// One established peer link: the post-handshake session, keepalive, per-link
/// staleness clock, and per-link send. LinkManager performs the hello exchange,
/// the gate, and routing, then hands the open connection + parsed hello here;
/// this class NEVER re-reads a hello. Port of the narrowed anyclip.py PeerLink
/// (session half). Exactly one connection, one peer, for the link's lifetime.
public sealed class PeerLink
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double MonotonicNow() => Clock.Elapsed.TotalSeconds;
    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private readonly FramedConnection _conn;
    private readonly bool _inbound;
    private volatile bool _alive = true;
    private volatile bool _superseded;
    private readonly double _linkedAt = MonotonicNow();
    // Monotonic timestamp of the last inbound frame. Drives half-open detection:
    // a peer that slept without RST/FIN keeps the socket "writable" yet sends
    // nothing back, so staleness is judged from inbound silence, not send errors.
    private double _lastInboundAt = MonotonicNow();

    public string PeerNodeId { get; }
    public string PeerName { get; }
    public int PeerProtocolMinor { get; }
    public string? RemoteHost => _conn.RemoteIp;
    /// The host:port label this link was dialed with (outbound), else null
    /// (inbound). LinkManager uses it to avoid redialing an already-linked peer.
    public string? DialLabel { get; }
    public double LinkedAt => _linkedAt;
    public bool IsActive => _alive;

    public Func<ClipPayload, Task>? OnClip { get; set; }
    public Action<DaemonEvent>? Emit { get; set; }

    public PeerLink(string peerNodeId, string peerName, VersionInfo peerVersion,
        FramedConnection conn, bool inbound, string? dialLabel)
    {
        PeerNodeId = peerNodeId;
        PeerName = peerName;
        PeerProtocolMinor = peerVersion.ProtocolMinor;
        _conn = conn;
        _inbound = inbound;
        DialLabel = dialLabel;
    }

    /// Mark this link as being replaced by a fresh connection for the same
    /// node_id: teardown then skips its LinkDown emit (the peer is still linked,
    /// just on a new socket). Mirrors the old single-PeerLink
    /// ReferenceEquals(_activeConn, framed) guard that suppressed the spurious
    /// LinkDown when one connection displaced another.
    public void MarkSuperseded() => _superseded = true;

    /// The receive loop. Emits LinkUp at entry (LinkManager has already
    /// registered this link in its node_id table), pumps frames until the
    /// connection closes, then emits LinkDown on teardown unless superseded.
    public async Task RunSessionAsync(CancellationToken ct)
    {
        RotatingLog.Shared.Info(
            $"linked with peer name={PeerName} id={PeerNodeId[..Math.Min(8, PeerNodeId.Length)]} "
            + $"({(_inbound ? "inbound" : "outbound")}) proto_minor={PeerProtocolMinor}");
        Emit?.Invoke(new LinkUp(PeerNodeId, PeerName));
        try
        {
            while (true)
            {
                WireMessage? msg;
                try { msg = await _conn.ReceiveMessageAsync(ct); }
                catch { break; }
                _lastInboundAt = MonotonicNow();      // any frame proves the peer is alive
                if (msg is null) break;
                switch (msg.Type)
                {
                    case "clip":
                        await HandleClipAsync(msg);
                        break;
                    case "ping":
                        try { await _conn.SendFrameAsync(WireMessage.Pong(UnixNow()), ct); }
                        catch (Exception e)
                        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
                        break;
                    case "pong":
                        break; // presence is enough
                    default:
                        RotatingLog.Shared.Debug($"ignoring message type: {msg.Type}");
                        break;
                }
            }
        }
        finally
        {
            _alive = false;
            RotatingLog.Shared.Info($"peer {PeerName} disconnected");
            if (!_superseded) Emit?.Invoke(new LinkDown(PeerNodeId, "peer disconnected"));
        }
    }

    private async Task HandleClipAsync(WireMessage msg)
    {
        var kind = msg.Kind ?? "text";
        switch (kind)
        {
            case "text" when msg.Content is not null:
                await (OnClip?.Invoke(new TextClip(msg.Content)) ?? Task.CompletedTask);
                break;
            case "image" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } png)
                    await (OnClip?.Invoke(new ImageClip(png)) ?? Task.CompletedTask);
                else RotatingLog.Shared.Warning("bad image payload from peer");
                break;
            case "file" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } data)
                {
                    var name = string.IsNullOrEmpty(msg.Name) ? "received.bin" : msg.Name!;
                    await (OnClip?.Invoke(new FileClip(name, data)) ?? Task.CompletedTask);
                }
                else RotatingLog.Shared.Warning("bad file payload from peer");
                break;
            case "files":
                if (msg.Files is null || msg.Files.Count == 0)
                {
                    RotatingLog.Shared.Warning("ignoring files clip with no entries");
                    break;
                }
                var decoded = new List<(string Name, byte[] Data)>(msg.Files.Count);
                bool bad = false;
                foreach (var entry in msg.Files)
                {
                    if (entry.Content is null ||
                        WireMessage.StrictBase64Decode(entry.Content) is not { } fbytes)
                    {
                        RotatingLog.Shared.Warning("bad file payload in files clip; ignoring frame");
                        bad = true;
                        break;
                    }
                    var fname = string.IsNullOrEmpty(entry.Name) ? "received.bin" : entry.Name!;
                    decoded.Add((fname, fbytes)); // hash NOT trusted from wire; recomputed downstream
                }
                if (!bad)
                    await (OnClip?.Invoke(new FilesClip(decoded)) ?? Task.CompletedTask);
                break;
            default:
                RotatingLog.Shared.Debug($"ignoring clip with kind={kind}");
                break;
        }
    }

    /// Per-link send. Returns false and DROPS the link on a hard send error
    /// (disposing the connection wakes the receive loop -> session tears down ->
    /// LinkManager removes it), so a broadcast failure isolates to this link.
    /// A too-large payload is dropped but the link is kept.
    public async Task<bool> SendClipAsync(ClipPayload payload)
    {
        if (!_alive) return false;
        try
        {
            await _conn.SendFrameAsync(WireMessage.Clip(payload, UnixNow()), CancellationToken.None);
            return true;
        }
        catch (PayloadTooLargeException e)
        { RotatingLog.Shared.Warning($"payload too large, dropping: {e.Message}"); return true; }
        catch (Exception e)
        {
            RotatingLog.Shared.Info($"send to {PeerName} failed; dropping link: {e.Message}");
            _conn.Dispose();
            return false;
        }
    }

    public async Task SendPingAsync()
    {
        if (!_alive) return;
        try { await _conn.SendFrameAsync(WireMessage.Ping(UnixNow()), CancellationToken.None); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"ping to {PeerName} failed: {e.Message}"); }
    }

    /// Seconds since the last inbound frame, or null if not linked. The per-link
    /// heartbeat compares this against its deadline.
    public double? SecondsSinceInbound() => _alive ? MonotonicNow() - _lastInboundAt : null;

    /// Drop a half-open link that has gone silent. Disposing the connection
    /// wakes the parked receive; the session loop tears down and clears the
    /// link. No-op if already unlinked.
    public void DropStaleLink(double idleSeconds)
    {
        if (!_alive) return;
        RotatingLog.Shared.Info(
            $"link to {PeerName} idle {(int)idleSeconds}s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect");
        _conn.Dispose();
    }

    public void Dispose()
    {
        _alive = false;
        _conn.Dispose();
    }
}
```

- [ ] **Step 3b: Add `LinkManager.cs`** — create `forwindows/src/AnyClipCore/LinkManager.cs`:
```csharp
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AnyClip.Core;

/// Per-link configuration shared by the manager. Moved out of PeerLink (which no
/// longer connects). Token-only config lives in config.json; the cap is a
/// constant + Python-only CLI flag, not persisted.
public sealed record LinkConfig(string Token, int Port, string Name, string AppVersion);

/// Owns the listening socket, the pre-routing gate (AuthGate ip-block/record +
/// token + version + self/loopback drop), and the active-link table keyed by
/// node_id. Accepts inbound and dials outbound; BOTH paths exchange hellos, run
/// the gate BEFORE any routing, then route to a per-peer PeerLink (create /
/// replace / refuse) handing it the open connection + parsed hello. Broadcasts
/// local clips to every active link with per-link protocol-minor gating, and
/// serializes received applies through one queue. Port of the LinkManager split
/// of anyclip.py PeerLink. The registration critical sections take a plain lock
/// and contain no awaits, mirroring the asyncio.Lock registration block.
public sealed class LinkManager
{
    public const int DefaultMaxPeers = 8;

    public readonly record struct BroadcastResult(int Sent, int OldPeerDrops);

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static double MonotonicNow() => Clock.Elapsed.TotalSeconds;

    private readonly LinkConfig _config;
    private readonly string _nodeId;
    private readonly int _maxPeers;
    private readonly double _linkPingInterval;
    private readonly string _tokenHash;
    private readonly AuthGate _authGate = new();
    private readonly object _lock = new();                    // guards _links, _authGate, _connecting
    private readonly SemaphoreSlim _applyLock = new(1, 1);    // serializes received applies
    private readonly Dictionary<string, PeerLink> _links = new();
    private readonly HashSet<string> _connecting = new();
    private TcpListener? _listener;

    public LinkManager(LinkConfig config, string nodeId,
        int maxPeers = DefaultMaxPeers, double linkPingInterval = 30)
    {
        _config = config;
        _nodeId = nodeId;
        _maxPeers = maxPeers;
        _linkPingInterval = linkPingInterval;
        _tokenHash = Hashing.Sha256Hex(config.Token);
    }

    /// Serialized received-clip sink: (payload, source peer display name). All
    /// links funnel through here under one lock so applies never interleave —
    /// the single-slot-per-kind suppressor stays sufficient with N peers.
    public Func<ClipPayload, string, Task>? OnClip { get; set; }
    public Action<DaemonEvent>? Emit { get; set; }
    public volatile bool IsServing;

    public int ActiveLinkCount { get { lock (_lock) return _links.Count; } }
    public bool AtCap { get { lock (_lock) return _links.Count >= _maxPeers; } }

    /// Linked peer display names, ordinal-sorted — for the "sent" toast target
    /// and the tray status line.
    public IReadOnlyList<string> LinkedPeerNames
    {
        get
        {
            lock (_lock)
                return _links.Values.Select(l => l.PeerName)
                    .OrderBy(n => n, StringComparer.Ordinal).ToList();
        }
    }

    /// True if any active link's remote IP is `host`. On a LAN a peer == one IP,
    /// so the reconnect loop uses this to avoid dialing a peer we already mesh
    /// with (inbound or outbound).
    public bool HasLinkToHost(string host)
    { lock (_lock) return _links.Values.Any(l => l.RemoteHost == host); }

    public async Task ServeAsync(CancellationToken ct)
    {
        if (_listener is not null)
            throw new FatalStartupException("ServeAsync called twice on the same LinkManager");

        TcpListener? listener = null;
        for (int attempt = 0; ; attempt++)
        {
            listener = new TcpListener(IPAddress.Any, _config.Port);
            // POSIX-only SO_REUSEADDR (parity with asyncio.start_server / macOS);
            // skipped on Windows where it has port-hijack semantics. Never permits
            // binding over an active LISTEN, so the bind-retry test still holds.
            if (!OperatingSystem.IsWindows())
                listener.Server.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { listener.Start(); break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Stop();
                if (attempt >= 4)
                    throw new FatalStartupException(
                        $"port {_config.Port} still in use after cleanup attempt; "
                        + "another process may have grabbed it");
                RotatingLog.Shared.Info(
                    $"tcp/{_config.Port} still in use; retrying bind ({attempt + 1}/4)");
                await Task.Delay(500, ct);
            }
        }
        _listener = listener;
        IsServing = true;
        RotatingLog.Shared.Info($"listening on tcp/{_config.Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (SocketException e) when (e.SocketErrorCode != SocketError.OperationAborted)
                { continue; }
                catch (Exception e) when (
                    e is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // Listen socket aborted out from under us (sleep/resume, NIC
                    // reset) without a clean Stop(). Throw the restart sentinel so
                    // the supervisor rebinds — otherwise RunOnceAsync's WhenAny sees
                    // a RanToCompletion serve task and silently exits with tcp/24816
                    // unbound (the Windows wedge).
                    if (!ct.IsCancellationRequested)
                        throw new DaemonRestartException(
                            $"listener accept aborted ({e.GetType().Name}); rebinding daemon "
                            + "(likely sleep/resume or network change)");
                    break;
                }
                _ = Task.Run(() => HandleInboundAsync(socket, ct), ct);
            }
        }
        finally { listener.Stop(); _listener = null; IsServing = false; }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        FramedConnection framed;
        try { framed = new FramedConnection(socket); }
        catch (SocketException) { socket.Dispose(); return; }
        RotatingLog.Shared.Debug($"inbound from {framed.RemoteIp ?? "?"}");
        bool blocked;
        lock (_lock) blocked = framed.RemoteIp is not null && _authGate.IsBlocked(framed.RemoteIp);
        if (blocked)
        {
            RotatingLog.Shared.Info(
                $"auth gate: {framed.RemoteIp} blocked (>{AuthGate.MaxFails} failures, "
                + $"cooldown {(int)AuthGate.CooldownSeconds}s)");
            framed.Dispose();
            return;
        }
        try { await HandleConnectionAsync(framed, inbound: true, label: null, ct); }
        catch (Exception e) { RotatingLog.Shared.Debug($"inbound session ended: {e.Message}"); }
    }

    public async Task TryConnectAsync(string host, int port, string label, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_links.Count >= _maxPeers) return;                      // no slots for a new peer
            if (_links.Values.Any(l => l.DialLabel == label)) return;   // already linked to this label
            if (!_connecting.Add(label))                                // dedupe in-flight dials
            { RotatingLog.Shared.Debug($"connect to {label} already in flight, skipping"); return; }
        }
        try
        {
            FramedConnection framed;
            try { framed = await FramedConnection.ConnectAsync(host, port, Wire.ConnectTimeoutSeconds, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            { RotatingLog.Shared.Info($"connect to {label} failed: {e.Message}"); return; }
            RotatingLog.Shared.Debug($"outbound connected to {label}");
            try { await HandleConnectionAsync(framed, inbound: false, label, ct); }
            catch (Exception e) { RotatingLog.Shared.Debug($"outbound session ended: {e.Message}"); }
        }
        finally { lock (_lock) _connecting.Remove(label); }
    }

    // Shared inbound/outbound flow: hello exchange -> gate -> route. Returns once
    // the link is registered (or rejected); the session runs detached so the
    // caller (accept loop / reconnect loop) is freed to service other peers.
    private async Task HandleConnectionAsync(
        FramedConnection framed, bool inbound, string? label, CancellationToken ct)
    {
        try { await framed.SendFrameAsync(WireMessage.Hello(
            _tokenHash, _nodeId, _config.Name, _config.AppVersion), ct); }
        catch { framed.Dispose(); return; }
        string addr = framed.RemoteIp ?? "";

        WireMessage? hello;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(Wire.HandshakeTimeoutSeconds));
            hello = await framed.ReceiveMessageAsync(hs.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RotatingLog.Shared.Warning("handshake timeout");
            Emit?.Invoke(new HandshakeFailed(addr, "timeout"));
            framed.Dispose(); return;
        }
        catch { framed.Dispose(); return; }

        if (hello is null || hello.Type != "hello")
        {
            RotatingLog.Shared.Warning("invalid hello, closing");
            Emit?.Invoke(new HandshakeFailed(addr, "invalid"));
            framed.Dispose(); return;
        }

        // --- Gate (before any routing) ---
        string? peerIp = inbound ? framed.RemoteIp : null;
        if (hello.Token != _tokenHash)
        {
            RotatingLog.Shared.Warning($"auth failed from peer name={hello.Name ?? "?"}");
            if (peerIp is not null) lock (_lock) _authGate.RecordFail(peerIp);
            Emit?.Invoke(new HandshakeFailed(peerIp ?? addr, "auth"));
            framed.Dispose(); return;
        }
        var peerVersion = hello.PeerVersionInfo();
        var localVersion = new VersionInfo(_config.AppVersion, Wire.ProtocolMajor, Wire.ProtocolMinor);
        var compat = VersionNegotiator.Negotiate(localVersion, peerVersion);
        if (!VersionNegotiator.LinkAllowed(compat))
        {
            RotatingLog.Shared.Warning(
                $"version refused: local proto={Wire.ProtocolMajor}.{Wire.ProtocolMinor} "
                + $"vs peer proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor} "
                + $"app={peerVersion.AppVersion} -> {VersionNegotiator.WireValue(compat)}");
            Emit?.Invoke(new HandshakeFailed(addr, $"version:{VersionNegotiator.WireValue(compat)}"));
            framed.Dispose(); return;
        }
        if (compat != Compatibility.Compatible)
            RotatingLog.Shared.Info($"version mismatch (link kept): {VersionNegotiator.WireValue(compat)}");
        var peerId = hello.NodeId;
        if (string.IsNullOrEmpty(peerId) || peerId == _nodeId)
        {
            RotatingLog.Shared.Debug("self loopback or bad node_id, dropping");
            framed.Dispose(); return;
        }
        if (peerIp is not null) lock (_lock) _authGate.RecordOk(peerIp);

        // --- Route (register / replace / cap; no awaits in the critical section) ---
        string displayName = string.IsNullOrEmpty(hello.Name)
            ? peerId[..Math.Min(8, peerId.Length)] : hello.Name!;
        PeerLink link;
        lock (_lock)
        {
            if (_links.TryGetValue(peerId, out var existing))
            {
                // Duplicate connection for a live node_id. Inside the handshake
                // window it's a genuine simultaneous-connect race -> tie-break
                // (lexicographic). Outside, the peer considers the old link dead
                // (our side half-open) -> replace, never defend.
                bool race = MonotonicNow() - existing.LinkedAt < Wire.RaceWindowSeconds;
                if (race)
                {
                    bool keepThisLink =
                        (!inbound && string.CompareOrdinal(_nodeId, peerId) < 0) ||
                        (inbound && string.CompareOrdinal(_nodeId, peerId) > 0);
                    if (!keepThisLink)
                    { RotatingLog.Shared.Debug("tie-breaker: dropping duplicate link (race)"); framed.Dispose(); return; }
                    RotatingLog.Shared.Debug("tie-breaker: replacing existing link (race)");
                }
                else
                {
                    RotatingLog.Shared.Info($"replacing link with {existing.PeerName} (peer reconnected)");
                }
                existing.MarkSuperseded(); // its teardown won't emit LinkDown
                existing.Dispose();
                _links.Remove(peerId);
            }
            else if (_links.Count >= _maxPeers)
            {
                // Cap is checked AFTER node_id routing, so a known peer reconnect
                // (handled above) is never refused; only a NEW node_id is.
                RotatingLog.Shared.Info($"peer cap reached ({_maxPeers}); refusing {displayName}");
                framed.Dispose(); return;
            }
            link = new PeerLink(peerId, displayName, peerVersion, framed, inbound, label);
            link.Emit = Emit;
            link.OnClip = p => DispatchReceiveAsync(p, displayName);
            _links[peerId] = link;
        }

        // Session + per-link staleness watchdog run detached.
        _ = Task.Run(() => RunLinkAsync(link, peerId, framed, ct), ct);
    }

    private async Task RunLinkAsync(
        PeerLink link, string peerId, FramedConnection framed, CancellationToken ct)
    {
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = Watchdogs.LinkPingLoopAsync(link, _linkPingInterval, linkCts.Token);
        try { await link.RunSessionAsync(linkCts.Token); }
        catch (Exception e) { RotatingLog.Shared.Debug($"link session ended: {e.Message}"); }
        finally
        {
            linkCts.Cancel();
            // Remove only if still the current entry — a replacement already
            // swapped in a new link under this node_id (ReferenceEquals guard,
            // ported from the old single-PeerLink teardown).
            lock (_lock)
            { if (_links.TryGetValue(peerId, out var cur) && ReferenceEquals(cur, link)) _links.Remove(peerId); }
            try { await ping; } catch { /* cancelled */ }
            framed.Dispose();
        }
        // Re-admission is handled by the reconnect loop: it re-scans the discovery
        // snapshot every cycle and dials any waiting peer whose slot just freed
        // (worst case one discovery/retry cycle — no new timer).
    }

    private async Task DispatchReceiveAsync(ClipPayload payload, string sourcePeerName)
    {
        await _applyLock.WaitAsync();
        try { if (OnClip is not null) await OnClip(payload, sourcePeerName); }
        finally { _applyLock.Release(); }
    }

    /// Fan out one local clip to every active link. Per-link protocol-minor
    /// gating: a minor-0 link gets the first-file legacy "file" fallback instead
    /// of "files". A per-link send failure drops only that link. OldPeerDrops is
    /// aggregated (max dropped count over old peers) so the caller emits ONE skip
    /// toast across all peers.
    public async Task<BroadcastResult> BroadcastAsync(ClipPayload payload)
    {
        List<PeerLink> targets;
        lock (_lock) targets = _links.Values.ToList();
        int sent = 0, oldPeerDrops = 0;
        foreach (var link in targets)
        {
            var toSend = payload;
            if (payload is FilesClip fc && link.PeerProtocolMinor < 1)
            {
                oldPeerDrops = Math.Max(oldPeerDrops, fc.Files.Count - 1);
                var (name, data) = fc.Files[0];
                toSend = new FileClip(name, data);
                RotatingLog.Shared.Info(
                    $"peer {link.PeerName} protocol minor {link.PeerProtocolMinor} < 1: "
                    + $"sending 1 of {fc.Files.Count} files");
            }
            if (await link.SendClipAsync(toSend)) sent++;
        }
        return new BroadcastResult(sent, oldPeerDrops);
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            foreach (var link in _links.Values) { link.MarkSuperseded(); link.Dispose(); }
            _links.Clear();
        }
        _listener?.Stop();
        IsServing = false;
    }
}
```

- [ ] **Step 3c: Rewire `Daemon.cs`** — four anchored edits in
  `forwindows/src/AnyClipCore/Daemon.cs`. The public `Daemon` API is unchanged.

  **(1) Replace the `PeerLink` wiring block.** Anchor on lines 107-199 (from
  `var link = new PeerLink(` through `clipboard.OnFileSkipped = ...;`):
```csharp
        var link = new PeerLink(
            new PeerLink.LinkConfig(config.Token, config.Port, config.Name, appVersion),
            nodeId);
        link.Emit = emit;
        link.OnClip = async payload =>
        {
            coordinator.MarkReceived(payload.Kind, payload.PayloadHash);
            string peer = link.PeerName ?? "peer";
            bool ok = await clipboard.ApplyRemoteAsync(payload);
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info(
                        $"<- received text {t.Text.Length} chars from {peer}");
                    toast($"AnyClip ← {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info(
                        $"<- received image {i.Png.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info(
                        $"<- received file {f.Name} {f.Data.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    // MarkReceived above recorded ("files", aggregate). A single
                    // placed file re-detects as a legacy "file" clip; suppress that
                    // too. (Windows places all N; N==1 only for a lenient 1-entry frame.)
                    if (fsc.Files.Count == 1)
                        coordinator.MarkReceived("file", Hashing.Sha256Hex(fsc.Files[0].Data));
                    RotatingLog.Shared.Info(
                        $"<- received {fsc.Files.Count} files from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"{fsc.Files.Count} files");
                    break;
            }
        };

        clipboard.OnLocalChange = async payload =>
        {
            if (!link.IsActive) return;
            if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
            {
                RotatingLog.Shared.Debug($"skip echo of just-received {payload.Kind}");
                return;
            }
            // Old-peer fallback: a peer on protocol minor 0 can't parse a
            // "files" clip. Downgrade to the first file as a legacy "file" clip
            // and report the dropped count via the skip-notification path.
            if (payload is FilesClip multi && link.PeerProtocolMinor < 1)
            {
                int dropped = multi.Files.Count - 1;
                var (fname, fdata) = multi.Files[0];
                payload = new FileClip(fname, fdata);
                if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
                {
                    RotatingLog.Shared.Debug("skip echo of just-received file (old-peer downgrade)");
                    return;
                }
                RotatingLog.Shared.Info(
                    $"peer protocol minor {link.PeerProtocolMinor} < 1: sending 1 of "
                    + $"{multi.Files.Count} files, {dropped} dropped");
                if (dropped > 0)
                    _ = clipboard.OnFileSkipped?.Invoke(
                        $"{dropped} file(s) not synced — update the peer's AnyClip for multi-file sync");
            }
            await link.SendClipAsync(payload);
            string peer = link.PeerName ?? "peer";
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"-> sent text {t.Text.Length} chars to {peer}");
                    toast($"AnyClip → {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info($"-> sent image {i.Png.Length} bytes to {peer}");
                    toast($"AnyClip → {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info($"-> sent file {f.Name} {f.Data.Length} bytes to {peer}");
                    toast($"AnyClip → {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    RotatingLog.Shared.Info($"-> sent {fsc.Files.Count} files to {peer}");
                    toast($"AnyClip → {peer}", $"{fsc.Files.Count} files");
                    break;
            }
        };
        clipboard.OnFileSkipped = msg => { toast("AnyClip", msg); return Task.CompletedTask; };
```
  Replace with (manager fan-out; received applies serialized in the manager; the
  per-link downgrade + aggregated skip toast move into the broadcast result):
```csharp
        var manager = new LinkManager(
            new LinkConfig(config.Token, config.Port, config.Name, appVersion), nodeId);
        manager.Emit = emit;
        // Received applies arrive already serialized through the manager's single
        // apply queue; mark the (global) suppressor BEFORE touching the clipboard.
        manager.OnClip = async (payload, peer) =>
        {
            coordinator.MarkReceived(payload.Kind, payload.PayloadHash);
            bool ok = await clipboard.ApplyRemoteAsync(payload);
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"<- received text {t.Text.Length} chars from {peer}");
                    toast($"AnyClip ← {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info(
                        $"<- received image {i.Png.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info(
                        $"<- received file {f.Name} {f.Data.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    if (fsc.Files.Count == 1)
                        coordinator.MarkReceived("file", Hashing.Sha256Hex(fsc.Files[0].Data));
                    RotatingLog.Shared.Info(
                        $"<- received {fsc.Files.Count} files from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"{fsc.Files.Count} files");
                    break;
            }
        };

        clipboard.OnLocalChange = async payload =>
        {
            if (manager.ActiveLinkCount == 0) return;
            if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
            {
                RotatingLog.Shared.Debug($"skip echo of just-received {payload.Kind}");
                return;
            }
            // Fan out to all links; per-link minor gating (files vs first-file
            // fallback) happens inside BroadcastAsync. OldPeerDrops is aggregated
            // so at most ONE skip toast fires for this local copy across all peers.
            var result = await manager.BroadcastAsync(payload);
            if (result.Sent == 0) return;
            if (result.OldPeerDrops > 0)
                _ = clipboard.OnFileSkipped?.Invoke(
                    $"{result.OldPeerDrops} file(s) not synced — update the peer's AnyClip for multi-file sync");
            string peers = string.Join(", ", manager.LinkedPeerNames);
            if (string.IsNullOrEmpty(peers)) peers = "peer";
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"-> sent text {t.Text.Length} chars to {peers}");
                    toast($"AnyClip → {peers}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info($"-> sent image {i.Png.Length} bytes to {peers}");
                    toast($"AnyClip → {peers}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info($"-> sent file {f.Name} {f.Data.Length} bytes to {peers}");
                    toast($"AnyClip → {peers}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    RotatingLog.Shared.Info($"-> sent {fsc.Files.Count} files to {peers}");
                    toast($"AnyClip → {peers}", $"{fsc.Files.Count} files");
                    break;
            }
        };
        clipboard.OnFileSkipped = msg => { toast("AnyClip", msg); return Task.CompletedTask; };
```

  **(2) Point discovery at the manager.** Anchor on lines 201-202:
```csharp
        var directory = new PeerDirectory(nodeId, emit,
            (host, port, label) => link.TryConnectAsync(host, port, label, outerCt));
```
  Replace with:
```csharp
        var directory = new PeerDirectory(nodeId, emit,
            (host, port, label) => manager.TryConnectAsync(host, port, label, outerCt));
```

  **(3) Rekey the task set.** Anchor on lines 221-229:
```csharp
        var tasks = new[]
        {
            link.ServeAsync(cts.Token),
            clipboard.RunAsync(cts.Token),
            Watchdogs.MdnsReconnectLoopAsync(directory, link, cts.Token),
            Watchdogs.NetworkWatchdogAsync(mdns, primaryIPv4, 15, cts.Token),
            Watchdogs.IdleLinkWatchdogAsync(mdns, link, 60, 3, cts.Token),
            Watchdogs.LinkPingLoopAsync(link, 30, cts.Token),
        };
```
  Replace with (per-link ping now runs inside the manager, one instance per link;
  the global idle escalator keys on the manager's active-link count):
```csharp
        var tasks = new[]
        {
            manager.ServeAsync(cts.Token),
            clipboard.RunAsync(cts.Token),
            Watchdogs.MdnsReconnectLoopAsync(directory, manager, cts.Token),
            Watchdogs.NetworkWatchdogAsync(mdns, primaryIPv4, 15, cts.Token),
            Watchdogs.IdleLinkWatchdogAsync(mdns, manager, 60, 3, cts.Token),
        };
```

  **(4) Shut down the manager.** Anchor on line 254 (inside the `finally`):
```csharp
            link.Shutdown();
```
  Replace with:
```csharp
            manager.Shutdown();
```

- [ ] **Step 3d: Rekey the watchdogs off the manager.** Two anchored edits in
  `forwindows/src/AnyClipCore/Watchdogs.cs`. `NetworkWatchdogAsync` and `LinkPingLoopAsync` are
  unchanged (`LinkPingLoopAsync` is now spawned per link by `LinkManager`).

  **(1) `IdleLinkWatchdogAsync` keys on the manager's active-link count** (fires ONLY when
  **zero** links are active — keyed per-link it would let one sleeping peer bounce the daemon and
  tear down every healthy link). Anchor on lines 68-92:
```csharp
    public static async Task IdleLinkWatchdogAsync(
        IMdnsService mdns, PeerLink link,
        double idleThresholdSeconds, int refreshAttempts, CancellationToken ct)
    {
        int consecutiveIdle = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(idleThresholdSeconds), ct);
            if (link.IsActive) { consecutiveIdle = 0; continue; }
            consecutiveIdle++;
            if (consecutiveIdle <= refreshAttempts)
            {
                RotatingLog.Shared.Info(
                    $"link idle {(int)(idleThresholdSeconds * consecutiveIdle)}s; "
                    + $"refreshing mDNS (attempt {consecutiveIdle}/{refreshAttempts})");
                mdns.Refresh();
            }
            else
            {
                throw new DaemonRestartException(
                    $"link idle with no recovery after {refreshAttempts} mDNS "
                    + "refresh attempts; bouncing daemon");
            }
        }
    }
```
  Replace with:
```csharp
    public static async Task IdleLinkWatchdogAsync(
        IMdnsService mdns, LinkManager manager,
        double idleThresholdSeconds, int refreshAttempts, CancellationToken ct)
    {
        int consecutiveIdle = 0;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(idleThresholdSeconds), ct);
            // Global escalator: only when the WHOLE mesh is down (zero links).
            if (manager.ActiveLinkCount > 0) { consecutiveIdle = 0; continue; }
            consecutiveIdle++;
            if (consecutiveIdle <= refreshAttempts)
            {
                RotatingLog.Shared.Info(
                    $"no active links for {(int)(idleThresholdSeconds * consecutiveIdle)}s; "
                    + $"refreshing mDNS (attempt {consecutiveIdle}/{refreshAttempts})");
                mdns.Refresh();
            }
            else
            {
                throw new DaemonRestartException(
                    $"no active links with no recovery after {refreshAttempts} mDNS "
                    + "refresh attempts; bouncing daemon");
            }
        }
    }
```

  **(2) `MdnsReconnectLoopAsync` dials every waiting peer (mesh), not just one.** Anchor on
  lines 94-150 (the entire current method):
```csharp
    public static async Task MdnsReconnectLoopAsync(
        PeerDirectory directory, PeerLink link, CancellationToken ct)
    {
        double backoff = 1;
        while (true)
        {
            if (link.IsActive)
            {
                backoff = 1;
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            var peers = directory.PeersSnapshot();
            if (peers.Count == 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            bool attempted = false;
            foreach (var (host, port, label) in peers)
            {
                if (link.IsActive) break;
                attempted = true;
                double start = MonotonicNow();
                await link.TryConnectAsync(host, port, label, ct);
                double elapsed = MonotonicNow() - start;
                if (link.IsActive)
                {
                    directory.ClearFails(label);
                    if (elapsed > 5) backoff = 1;
                    break;
                }
                if (elapsed > 5)
                {
                    // Long session that later died — healthy peer, not a
                    // prune candidate.
                    directory.ClearFails(label);
                    continue;
                }
                int fails = directory.RecordFail(label);
                if (fails >= Wire.MaxReconnectFails)
                {
                    directory.PruneAddress(label);
                    RotatingLog.Shared.Info(
                        $"pruned stale peer address {label} after {fails} failed "
                        + "attempts; awaiting fresh mDNS discovery");
                }
            }
            if (link.IsActive) continue;
            if (attempted)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(backoff, 60)), ct);
                backoff = Math.Min(backoff * 2, 60);
            }
            else await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }
```
  Replace with (each cycle dials all discovered peers that are unlinked and under cap; the
  manager's session now runs detached, so `TryConnectAsync` returns after registration and a
  freed cap slot is re-admitted on the next cycle):
```csharp
    public static async Task MdnsReconnectLoopAsync(
        PeerDirectory directory, LinkManager manager, CancellationToken ct)
    {
        double backoff = 1;
        while (true)
        {
            var peers = directory.PeersSnapshot();
            bool attempted = false;
            foreach (var (host, port, label) in peers)
            {
                if (ct.IsCancellationRequested) return;
                if (manager.AtCap) break;                    // no slots: stop dialing this cycle
                if (manager.HasLinkToHost(host)) continue;   // already meshed with this peer
                attempted = true;
                await manager.TryConnectAsync(host, port, label, ct);
                if (manager.HasLinkToHost(host))
                {
                    directory.ClearFails(label);
                    continue;
                }
                int fails = directory.RecordFail(label);
                if (fails >= Wire.MaxReconnectFails)
                {
                    directory.PruneAddress(label);
                    RotatingLog.Shared.Info(
                        $"pruned stale peer address {label} after {fails} failed "
                        + "attempts; awaiting fresh mDNS discovery");
                }
            }
            if (attempted)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(backoff, 60)), ct);
                backoff = Math.Min(backoff * 2, 60);
            }
            else { backoff = 1; await Task.Delay(TimeSpan.FromSeconds(2), ct); }
        }
    }
```
  (`MonotonicNow` may now be unused; if the C# compiler warns, delete the private
  `MonotonicNow`/`Clock` members at the top of `Watchdogs` — nothing else references them.)

- [ ] **Step 3e: Convert the existing interop tests to `LinkManager`.** `PeerLink` no longer
  connects, so the three tests must drive a `LinkManager`. Replace
  `forwindows/tests/AnyClipCore.Tests/InteropTests.cs` entirely (the `RepoRoot`/`ReadShared`
  helpers are kept verbatim; Task 9 appends the two-peer test). The former
  `InteropSendsFilesClipToPythonPeer` is reframed as a **downgrade** check: the shared
  `fake_peer.py` advertises `protocol_minor: 0`, so the mesh broadcast correctly downgrades a
  files clip to the first-file legacy `"file"` for it. The C#-encoder→Python "files" wire path
  stays pinned by `GoldenVectorTests` (byte-exact) and the receive direction below.
```csharp
using System.Diagnostics;
using System.Runtime.CompilerServices;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class InteropTests
{
    private static string RepoRoot([CallerFilePath] string path = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, "..", "..", ".."));

    // fake_peer.py keeps the out-file open for write for the whole session;
    // File.ReadAllText opens with FileShare.Read, a sharing violation on Windows
    // against that write handle. Do NOT "simplify" back to File.ReadAllText.
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    private static ProcessStartInfo FakePeerPsi(int port, string outFile, bool sendFiles = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "python3",
            ArgumentList =
            {
                Path.Combine(RepoRoot(), "formacOS", "Scripts", "fake_peer.py"),
                "--port", port.ToString(),
                "--token", "interop-token",
                "--out", outFile,
            },
            RedirectStandardOutput = true,
        };
        if (sendFiles) psi.ArgumentList.Add("--send-files");
        return psi;
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    [Fact]
    public async Task InteropWithPythonFakePeer()
    {
        int port = 28631;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var events = new List<DaemonEvent>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28632, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = e => { lock (events) events.Add(e); };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);

            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));
            lock (events) Assert.Contains(events, e => e is LinkUp u && u.PeerName == "fake-peer");
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Any(c => c.Payload is TextClip t && t.Text == "hello-from-python"); }));

            await manager.BroadcastAsync(new TextClip("hello-from-csharp"));
            await manager.BroadcastAsync(new ImageClip(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }));
            await manager.BroadcastAsync(new FileClip("노트.txt", "file-content"u8.ToArray()));

            Assert.True(await WaitUntil(() =>
            {
                if (!File.Exists(outFile)) return false;
                var lines = ReadShared(outFile);
                return lines.Contains("hello-from-csharp")
                    && lines.Contains("\"kind\": \"file\"")
                    && lines.Contains("노트.txt")
                    && lines.Contains("\"kind\": \"image\"");
            }));

            var outText = ReadShared(outFile);
            var helloLine = outText.Split('\n').FirstOrDefault(l => l.Contains("\"event\": \"hello\""));
            Assert.NotNull(helloLine);
            Assert.Contains("\"version\": 1", helloLine);
            Assert.Contains("\"protocol_major\": 1", helloLine);

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public async Task InteropDowngradesFilesClipToOldPythonPeer()
    {
        int port = 28633;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var manager = new LinkManager(
                new LinkConfig("interop-token", 28634, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (_, _) => Task.CompletedTask;
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));

            // fake_peer advertises protocol_minor 0 -> the broadcast downgrades the
            // 2-file clip to the first file as a legacy "file", and reports 1 drop.
            var res = await manager.BroadcastAsync(new FilesClip(new List<(string, byte[])>
            {
                ("노트.txt", "multi body one"u8.ToArray()),
                ("(E&S) plan.txt", "multi body two"u8.ToArray()),
            }));
            Assert.Equal(1, res.OldPeerDrops);

            Assert.True(await WaitUntil(() =>
            {
                if (!File.Exists(outFile)) return false;
                var lines = ReadShared(outFile);
                return lines.Contains("\"kind\": \"file\"") && lines.Contains("노트.txt");
            }));
            // Never a multi-file "files" frame to a minor-0 peer.
            Assert.DoesNotContain("\"kind\": \"files\"", ReadShared(outFile));

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }

    [Fact]
    public async Task InteropReceivesFilesClipFromPythonPeer()
    {
        int port = 28635;
        string outFile = Path.Combine(Path.GetTempPath(), $"fake-peer-{Guid.NewGuid()}.jsonl");
        using var proc = Process.Start(FakePeerPsi(port, outFile, sendFiles: true))!;
        try
        {
            var ready = await proc.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("READY", ready);

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28636, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", port, $"127.0.0.1:{port}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 1));
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Any(c => c.Payload is FilesClip f && f.Files.Count == 2); }));

            FilesClip got;
            lock (clips) got = clips.Select(c => c.Payload).OfType<FilesClip>().First(f => f.Files.Count == 2);
            Assert.Equal("노트.txt", got.Files[0].Name);
            Assert.Equal("multi body one", System.Text.Encoding.UTF8.GetString(got.Files[0].Data));
            Assert.Equal("(E&S) plan.txt", got.Files[1].Name);
            Assert.Equal("multi body two", System.Text.Encoding.UTF8.GetString(got.Files[1].Data));

            manager.Shutdown();
        }
        finally { if (!proc.HasExited) proc.Kill(); }
    }
}
```

- [ ] **Step 3f: Keep `DaemonTests` compiling and green.** `DaemonTests.OldPeerDowngrades...`
  drives the daemon end-to-end through a raw minor-0 inbound peer and asserts a legacy `file`
  frame + a `1 file` toast. The behavior is preserved (the downgrade + aggregated skip toast now
  come through `LinkManager.BroadcastAsync` + `BroadcastResult.OldPeerDrops`), and the test uses
  only public `Daemon`/`WireMessage` surface — **no edit needed**. Verify by running it in Step 4.
  If, after Step 3a-3e, the compiler flags any residual `PeerLink.LinkConfig`/`link.` reference in
  a test file, it is a leftover from this task's own edits — fix it to the `LinkConfig`/`manager`
  form shown above; there are no other production references.

- [ ] **Step 4: Run to see it pass** —
  `dotnet test forwindows/tests/AnyClipCore.Tests`
  Expected: whole `AnyClipCore.Tests` suite green. In particular: `LinkManagerTests` (9 tests —
  handshake+broadcast, wrong-token auth event, major-mismatch version event, bind-retry, cap
  refuses a new node_id, known-node_id reconnect-at-cap replaces with no spurious `LinkDown`,
  dead link frees the slot, files downgrade vs files per link, invalid/empty files frame
  ignored + link stays up); `InteropTests` (3, requires `python3` on PATH); `DaemonTests`
  (`OldPeerDowngradesFilesClipToFirstFileWithNotification` still green);
  `PeerStateTests`/`WireMessageTests`/`GoldenVectorTests`/`PeerDirectoryTests` unaffected.

- [ ] **Step 5: Commit**
```
git add forwindows/src/AnyClipCore/PeerLink.cs forwindows/src/AnyClipCore/LinkManager.cs \
        forwindows/src/AnyClipCore/Daemon.cs forwindows/src/AnyClipCore/Watchdogs.cs \
        forwindows/tests/AnyClipCore.Tests/LinkManagerTests.cs \
        forwindows/tests/AnyClipCore.Tests/InteropTests.cs
git rm forwindows/tests/AnyClipCore.Tests/PeerLinkTests.cs
git commit -m "$(cat <<'EOF'
feat(win): LinkManager full mesh — split PeerLink, broadcast to all links

Add LinkManager (listening socket + AuthGate + node_id-keyed active-link table
+ pre-routing gate + broadcast + serialized receive) and narrow PeerLink to one
post-handshake session. Route by node_id: new node at cap refused, known-node
reconnect replaces (tie-break only inside the race window), dead link frees its
slot. Per-link protocol-minor gating in the broadcast loop; per-link staleness
watchdog per link; the mDNS idle escalator now keys on zero active links.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

---

### Task 9: tray peer-list rendering + watchdog/skip-toast coverage + two-peer interop

Add the pure tray status renderer and wire the tray to it; lock in the rekeyed watchdogs
(global escalator only at zero links; per-link staleness drops only its own link) and the
aggregated skip toast with regression tests; and add the spec-mandated two-`fake_peer` interop
test (both handshake, one local clip reaches both, non-relay asserted by a bounded drain of
peer B).

**Files:**
- Create: `forwindows/src/AnyClipCore/PeerStatus.cs`
- Modify: `forwindows/src/AnyClipApp/TrayIcon.cs` (`Apply` uses `PeerStatus.Line`; **Windows-only,
  not built by the macOS test command**)
- Create: `forwindows/tests/AnyClipCore.Tests/PeerStatusTests.cs`
- Create: `forwindows/tests/AnyClipCore.Tests/WatchdogRekeyTests.cs`
- Modify: `forwindows/tests/AnyClipCore.Tests/DaemonTests.cs` (add the aggregation test)
- Modify: `forwindows/tests/AnyClipCore.Tests/InteropTests.cs` (append the two-peer test)

**Interfaces:**
- Consumes (Task 7): `PeerUiState`, `PeerStateReducer`, `LinkUp`/`LinkDown`, `PeerStateKind`.
  (Task 8): `LinkManager` (`TryConnectAsync`, `BroadcastAsync`, `ActiveLinkCount`, `IsServing`,
  `Shutdown`, `OnClip`, `Emit`, `linkPingInterval` ctor arg), `LinkConfig`,
  `Watchdogs.IdleLinkWatchdogAsync(IMdnsService, LinkManager, …)`, `LinkDown.NodeId`. Existing:
  `FramedConnection`, `WireMessage`, `Hashing`, `FakeMdns`/`FakePidLock`/`FakeClipboard` (internal
  in `DaemonTests.cs`, same assembly), `DaemonRestartException`.
- Produces:
  - `static class PeerStatus { string Line(PeerUiState s) }` — zero peers keeps the pre-mesh
    text (Idle/Searching/Error); ≥1 peer → `"Linked: "` + names ordinal-sorted, comma-joined.

#### Part A — tray status rendering

- [ ] **Step A1: Write the failing test** — create `forwindows/tests/AnyClipCore.Tests/PeerStatusTests.cs`:
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStatusTests
{
    [Fact]
    public void ZeroPeersKeepsPreMeshText()
    {
        Assert.Equal("Idle", PeerStatus.Line(PeerUiState.Initial));
        var searching = PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1);
        Assert.Equal("Searching for peer", PeerStatus.Line(searching));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("network"), 1);
        Assert.Equal("Error: network", PeerStatus.Line(err));
    }

    [Fact]
    public void LinkedListsPeersOrdinalSortedCommaJoined()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("id-1", "win-pc"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("id-2", "mac-air"), 2);
        Assert.Equal("Linked: mac-air, win-pc", PeerStatus.Line(s)); // sorted by name, ordinal
        s = PeerStateReducer.Reduce(s, new LinkDown("id-1", "x"), 3);
        Assert.Equal("Linked: mac-air", PeerStatus.Line(s));
        s = PeerStateReducer.Reduce(s, new LinkDown("id-2", "peer disconnected"), 4);
        Assert.Equal("Searching for peer", PeerStatus.Line(s));
    }
}
```

- [ ] **Step A2: Run to see it fail** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter PeerStatusTests`
  Expected: **compile failure** — `PeerStatus` does not exist.

- [ ] **Step A3: Add `PeerStatus.cs` and wire the tray.**
  Create `forwindows/src/AnyClipCore/PeerStatus.cs`:
```csharp
namespace AnyClip.Core;

/// Human status line for the tray/menu. Zero peers keeps the pre-mesh
/// presentation; >=1 peer -> "Linked: " + display names sorted ordinally,
/// comma-joined. Keep in lockstep with the Swift StatusItemController and the
/// Python app shells.
public static class PeerStatus
{
    public static string Line(PeerUiState s)
    {
        if (s.Peers.Count > 0)
            return "Linked: " + string.Join(", ",
                s.Peers.Values.OrderBy(n => n, StringComparer.Ordinal));
        return s.Kind switch
        {
            PeerStateKind.Searching => "Searching for peer",
            PeerStateKind.Error => $"Error: {s.Reason ?? "unknown"}",
            _ => "Idle",
        };
    }
}
```
  Then wire the tray (**App is Windows-only; this edit is not built by the macOS test command,
  verified on Windows CI**) — Edit `forwindows/src/AnyClipApp/TrayIcon.cs`, `Apply`. Anchor on
  the status switch (the Task 7 form):
```csharp
        string status = state.Kind switch
        {
            PeerStateKind.Linked => "Linked: " + (state.Peers.Count > 0
                ? string.Join(", ", state.Peers.Values) : "peer"),
            PeerStateKind.Searching => "Searching for peer",
            PeerStateKind.Error => $"Error: {state.Reason ?? "unknown"}",
            _ => "Idle",
        };
```
  Replace with:
```csharp
        string status = PeerStatus.Line(state);
```
  (`_lastSyncItem`, the icon spec, and the tooltip below are unchanged — they already key on
  `state.Kind`, which is `Linked` iff peers are present.)

- [ ] **Step A4: Run to see it pass** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter PeerStatusTests`
  Expected: 2 tests pass.

- [ ] **Step A5: Commit**
```
git add forwindows/src/AnyClipCore/PeerStatus.cs forwindows/src/AnyClipApp/TrayIcon.cs \
        forwindows/tests/AnyClipCore.Tests/PeerStatusTests.cs
git commit -m "$(cat <<'EOF'
feat(win): tray status renders the linked-peer list

Add PeerStatus.Line (pure, cross-platform tested): zero peers keeps the
pre-mesh Idle/Searching/Error text; one or more peers shows "Linked: " + the
display names sorted ordinally and comma-joined. Wire TrayIcon.Apply to it.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

#### Part B — watchdog rekey + skip-toast aggregation coverage

> These lock in behavior implemented in Task 8 (the mDNS escalator keying on **zero** active links,
> per-link staleness dropping only the silent link, and the single aggregated downgrade toast).
> They are regression tests: they pass against the Task 8 code. Run them and confirm green.

- [ ] **Step B1: Write the tests.** Create `forwindows/tests/AnyClipCore.Tests/WatchdogRekeyTests.cs`
  (reuses the internal `FakeMdns` from `DaemonTests.cs` — same assembly):
```csharp
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class WatchdogRekeyTests
{
    private static async Task<bool> WaitUntil(Func<bool> cond, double seconds = 6)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private static async Task<FramedConnection> RawHandshake(
        int port, string node, string name, CancellationToken ct)
    {
        var raw = await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("tok"), node, name, "0.0.0-test"), ct);
        _ = await raw.ReceiveMessageAsync(ct);
        return raw;
    }

    [Fact]
    public async Task EscalatorRefreshesThenBouncesWhenZeroLinks()
    {
        var mdns = new FakeMdns();
        var m = new LinkManager(new LinkConfig("tok", 28731, "esc", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wd = Watchdogs.IdleLinkWatchdogAsync(mdns, m, 0.2, 2, cts.Token);
        await Assert.ThrowsAsync<DaemonRestartException>(async () => await wd);
        Assert.True(mdns.Refreshes >= 2); // two refresh attempts before the bounce
    }

    [Fact]
    public async Task EscalatorNeverFiresWhileALinkIsActive()
    {
        var mdns = new FakeMdns();
        var m = new LinkManager(new LinkConfig("tok", 28732, "esc", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));
        using var raw = await RawHandshake(28732, "node-live", "live", cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        using var wdCts = new CancellationTokenSource();
        var wd = Watchdogs.IdleLinkWatchdogAsync(mdns, m, 0.2, 2, wdCts.Token);
        await Task.Delay(1000);
        Assert.Equal(0, mdns.Refreshes); // a live link -> escalator stays quiet
        Assert.False(wd.IsFaulted);

        wdCts.Cancel(); cts.Cancel(); m.Shutdown();
        try { await wd; } catch (OperationCanceledException) { }
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PerLinkStalenessDropsOnlyTheSilentLink()
    {
        var events = new List<DaemonEvent>();
        var m = new LinkManager(new LinkConfig("tok", 28733, "stale", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant(), linkPingInterval: 0.3);
        m.OnClip = (_, _) => Task.CompletedTask;
        m.Emit = e => { lock (events) events.Add(e); };
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        // Live peer answers pings with pongs -> refreshes its inbound clock.
        var rawLive = await RawHandshake(28733, "node-live", "live", cts.Token);
        var pongPump = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var msg = await rawLive.ReceiveMessageAsync(cts.Token);
                    if (msg is null) break;
                    if (msg.Type == "ping") await rawLive.SendFrameAsync(WireMessage.Pong(1), cts.Token);
                }
            }
            catch { }
        });
        // Silent peer never pongs.
        var rawSilent = await RawHandshake(28733, "node-silent", "silent", cts.Token);

        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));
        // 0.3s interval * deadFactor 3 = 0.9s of silence -> the silent link drops.
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1, 8));
        lock (events)
        {
            Assert.Contains(events, e => e is LinkDown d && d.NodeId == "node-silent");
            Assert.DoesNotContain(events, e => e is LinkDown d && d.NodeId == "node-live");
        }

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
        try { await pongPump; } catch { }
        rawLive.Dispose();
    }
}
```
  Then append the aggregation test to `forwindows/tests/AnyClipCore.Tests/DaemonTests.cs` — add
  this method inside `public class DaemonTests` (it reuses the class-local `ConnectWithRetry`):
```csharp
    [Fact]
    public async Task DowngradeSkipToastAggregatedToOneAcrossOldPeers()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-agg-" + Guid.NewGuid());
        var clip = new FakeClipboard();
        var notes = new List<string>();
        var daemon = new Daemon(
            new DaemonConfig("agg-token", 28626, "agg", NotificationsEnabled: true),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
            primaryIPv4: () => "127.0.0.1",
            notify: (_, body) => { lock (notes) notes.Add(body); }, onFatal: _ => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = daemon.RunForeverAsync(cts.Token);

        async Task<FramedConnection> ConnectOld(string node, string name)
        {
            var raw = await ConnectWithRetry(28626, cts.Token);
            await raw.SendFrameAsync(WireMessage.Hello(
                Hashing.Sha256Hex("agg-token"), node, name, "1.0.0") with { ProtocolMinor = 0 },
                cts.Token);
            _ = await raw.ReceiveMessageAsync(cts.Token);
            return raw;
        }
        using var rawA = await ConnectOld("old-a", "old-a");
        using var rawB = await ConnectOld("old-b", "old-b");

        // Drain LinkUp events until both old peers are linked.
        async Task<bool> WaitLinks(int n, double seconds = 12)
        {
            int count = 0;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                while (daemon.Events.TryRead(out var ev)) if (ev is LinkUp) count++;
                if (count >= n) return true;
                await Task.Delay(50);
            }
            return count >= n;
        }
        Assert.True(await WaitLinks(2));
        Assert.NotNull(clip.OnLocalChange);

        await clip.OnLocalChange!(new FilesClip(new List<(string, byte[])>
        {
            ("a.txt", "one"u8.ToArray()),
            ("b.txt", "two"u8.ToArray()),
            ("c.txt", "three"u8.ToArray()),
        }));

        async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }
        // ONE aggregated skip toast for the whole copy, not one per old peer.
        Assert.True(await WaitUntil(() =>
        { lock (notes) return notes.Count(n => n.Contains("not synced")) == 1; }));
        lock (notes) Assert.Contains(notes, n => n.Contains("2 file(s) not synced"));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }
```

- [ ] **Step B2: Run to confirm green** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter "WatchdogRekeyTests|DaemonTests"`
  Expected: `WatchdogRekeyTests` (3) and all `DaemonTests` (incl. the new
  `DowngradeSkipToastAggregatedToOneAcrossOldPeers`) pass — locking in Task 8's zero-links
  escalator keying, per-link staleness isolation, and the single aggregated downgrade toast.

- [ ] **Step B3: Commit**
```
git add forwindows/tests/AnyClipCore.Tests/WatchdogRekeyTests.cs \
        forwindows/tests/AnyClipCore.Tests/DaemonTests.cs
git commit -m "$(cat <<'EOF'
test(win): mesh watchdog rekey + aggregated downgrade toast

Regression coverage for the mesh watchdogs: the mDNS idle escalator fires only
when zero links are active (and stays quiet while any link is up), and the
per-link staleness dropper takes down only the silent link. Plus: two old
(minor-0) peers on one local copy yield exactly one aggregated skip toast.

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

#### Part C — two-`fake_peer` interop (broadcast + non-relay)

- [ ] **Step C1: Append the two-peer interop test** to `forwindows/tests/AnyClipCore.Tests/InteropTests.cs`
  — add this method inside `public class InteropTests` (it reuses `FakePeerPsi`, `ReadShared`,
  `WaitUntil` from Step 3e):
```csharp
    [Fact]
    public async Task InteropTwoPeersReceiveBroadcastAndNoRelay()
    {
        int portA = 28637, portB = 28638;
        string outA = Path.Combine(Path.GetTempPath(), $"fake-peer-A-{Guid.NewGuid()}.jsonl");
        string outB = Path.Combine(Path.GetTempPath(), $"fake-peer-B-{Guid.NewGuid()}.jsonl");
        using var procA = Process.Start(FakePeerPsi(portA, outA))!;
        using var procB = Process.Start(FakePeerPsi(portB, outB))!;
        try
        {
            Assert.Equal("READY", await procA.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal("READY", await procB.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            var clips = new List<(ClipPayload Payload, string Peer)>();
            var manager = new LinkManager(
                new LinkConfig("interop-token", 28639, "csharp-interop", "0.0.0-test"),
                Guid.NewGuid().ToString().ToLowerInvariant());
            manager.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
            manager.Emit = _ => { };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await manager.TryConnectAsync("127.0.0.1", portA, $"127.0.0.1:{portA}", cts.Token);
            await manager.TryConnectAsync("127.0.0.1", portB, $"127.0.0.1:{portB}", cts.Token);
            Assert.True(await WaitUntil(() => manager.ActiveLinkCount == 2));

            // Both peers pushed their own "hello-from-python" clip; both applied locally.
            Assert.True(await WaitUntil(() =>
            { lock (clips) return clips.Count(c => c.Payload is TextClip t && t.Text == "hello-from-python") >= 2; }));

            // One local clip broadcasts to BOTH peers.
            await manager.BroadcastAsync(new TextClip("mesh-broadcast"));
            Assert.True(await WaitUntil(() => File.Exists(outA) && ReadShared(outA).Contains("mesh-broadcast")));
            Assert.True(await WaitUntil(() => File.Exists(outB) && ReadShared(outB).Contains("mesh-broadcast")));

            // Non-relay: peer A's clip was applied locally, NEVER forwarded to B.
            // Drain B for a bounded interval; every text clip B received is our
            // broadcast, never a relayed "hello-from-python" (this doubles as the
            // echo-suppression-under-mesh check).
            await Task.Delay(1000);
            var recvTextFrames = ReadShared(outB).Split('\n')
                .Where(l => l.Contains("\"event\": \"recv\"") && l.Contains("\"kind\": \"text\""))
                .ToList();
            Assert.NotEmpty(recvTextFrames);
            Assert.All(recvTextFrames, l => Assert.DoesNotContain("hello-from-python", l));
            Assert.Contains(recvTextFrames, l => l.Contains("mesh-broadcast"));

            manager.Shutdown();
        }
        finally
        {
            if (!procA.HasExited) procA.Kill();
            if (!procB.HasExited) procB.Kill();
        }
    }
```

- [ ] **Step C2: Run to confirm green** — `dotnet test forwindows/tests/AnyClipCore.Tests --filter InteropTests`
  Expected: all 4 `InteropTests` pass (requires `python3` on PATH). The new test proves both
  peers handshake, one local clip reaches both, and peer A's clip is not relayed to B.

- [ ] **Step C3: Commit**
```
git add forwindows/tests/AnyClipCore.Tests/InteropTests.cs
git commit -m "$(cat <<'EOF'
test(win): two-peer fake_peer interop — broadcast reaches both, no relay

Spawn two fake_peer.py instances against one LinkManager: both handshake, a
local clip broadcasts to both, and peer A's clip — applied locally — is never
relayed to peer B (bounded drain of B's socket asserts zero relayed clips).

Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step C4: Full-suite sanity** — `dotnet test forwindows/tests/AnyClipCore.Tests`
  Expected: the entire `AnyClipCore.Tests` suite is green (routing/broadcast/cap/replacement,
  per-link gating, serialized applies, watchdog rekey, tray status, aggregation, 4 interop).
  No golden-vector regeneration and no `Wire`/`WireMessage` change — `GoldenVectorTests` passing
  byte-identical fixtures doubles as proof the wire is unchanged.

---

## Notes for assembly

- **Task numbers** Task 7/Task 8/Task 9 are placeholders; renumber on assembly.
- **App verification gap:** every `AnyClipApp` edit (TrayIcon in Task 7 and Task 9) is WinForms and is
  built/tested only on Windows CI (`dotnet test forwindows/tests/AnyClipApp.Tests`), never by the
  macOS `AnyClipCore.Tests` command. The tray status *logic* is covered cross-platform by
  `PeerStatusTests`; the WinForms `Apply` glue is exercised by the Windows-only `TrayIconTests`
  render smoke test (unchanged).
- **`fake_peer.py` untouched** (shared with the Swift interop): the C# send-side "files" wire path
  is covered by `GoldenVectorTests` (byte-exact) + the receive-direction interop; the send-files
  interop is reframed as the minor-0 downgrade check, so no shared-fixture edit is needed.
- **Python-only `--max-peers` CLI flag** is out of scope here; the C# cap is the
  `LinkManager.DefaultMaxPeers = 8` constant, and `config.json` stays token-only.

---

# Part 4 — Docs (Task 10)

### Task 10: README multi-peer documentation

**Files:**
- Modify: `README.md` (the "1:1 피어만 지원" limitation bullet and the "How it works" section)

**Interfaces:**
- Consumes: the shipped behavior of Tasks 1–9 (mesh, cap 8, no relay).
- Produces: nothing downstream — docs only.

- [ ] **Step 1: Update the limitation bullet**

In `README.md`, find the limitations list entry:

```markdown
- 1:1 피어만 지원 (3대 이상 동시 동기화 미지원)
```

Replace with:

```markdown
- 풀 메시 다중 피어 지원 (동시 최대 8대, 릴레이 없음 — 모든 기기가 같은 LAN에서 서로를 직접 볼 수 있어야 함)
- 다중 피어는 모든 기기가 1.3.0 이상일 때 완전 동작 (구버전 피어는 기존처럼 링크를 독점하려 함)
```

- [ ] **Step 2: Mention the mesh in "How it works"**

In the README "How it works" section, after the sentence describing the peer link, add one sentence:

```markdown
1.3.0부터 데몬은 발견된 같은 토큰의 피어 전부와 동시에 링크를 유지하고(기본 최대 8대), 복사된 클립을 모든 활성 링크에 브로드캐스트한다. 수신한 클립을 다른 피어로 중계하지는 않는다.
```

- [ ] **Step 3: Update the local CLAUDE.md (do NOT commit it)**

`CLAUDE.md` is untracked by design in this repo. Edit the working copy so future agent sessions see the new shape — in the "Layered architecture" section, note that each daemon now runs a `LinkManager` owning N per-peer `PeerLink`s (full mesh, cap 8, no relay) — but leave the file out of the commit.

- [ ] **Step 4: Commit**

```bash
git add README.md
git commit -m "docs: document full-mesh multi-peer (cap 8, no relay)"
```
