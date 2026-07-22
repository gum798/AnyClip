# Desktop multi-peer (1:N full mesh)

**Date:** 2026-07-22
**Status:** Approved

## Problem

Every daemon owns exactly **one** active TCP link (`anyclip.py:1300`
"Owns the single active TCP link to a peer"; same single-`link` shape in
Swift `Daemon.swift` and C# `Daemon.cs`). With three devices on the LAN
sharing a token, link-stealing thrashes: a third device connecting
displaces the existing pair, the displaced peer reconnects and displaces
back, repeatedly. This blocks the planned Android client (README lists
Android as a future phase), whose "receive-auto" UX needs a persistent
link **in addition to** the existing Mac↔Windows pair.

## Decision

Full mesh: every device links directly to every discovered same-token
peer (up to a cap) and local clips broadcast to **all** active links.
No relay/forwarding — a mesh makes it unnecessary and its absence rules
out forwarding loops by construction. All devices must therefore see
each other on the LAN (documented limitation, same trust model as
today).

**No wire change.** Protocol stays 1.1; hello, framing, golden vectors,
and the interop fixtures are untouched. Shipped as app version 1.3.0.

Alternatives rejected:

- **Hub-relay** (one device forwards for the rest): needs relay logic,
  loop prevention, hub election, and creates a single point of failure;
  breaks the symmetric P2P shape all three daemons share.
- **On-demand links** (connect, sync, disconnect): incompatible with
  receive-auto UX; keeps the thrash window on every connect.

## Compatibility

No wire change means 1.3.0 links with 1.2.x and earlier exactly as
today, pairwise. But an un-upgraded peer still owns a single link and
still steals it — the motivating 3-device scenario is only fully
resolved once **every** device in the group runs ≥ 1.3.0. A mixed
group degrades to today's behavior around the old device, with the
upgraded devices meshing among themselves.

## Architecture: split PeerLink into LinkManager + PeerLink

Today `PeerLink` owns the TCP server, outbound connects, and the single
session — with the hello exchange, token check, version gate, and
self-loopback drop all inline in the session before registration
(Python `_session` at `anyclip.py:1435`, Swift `PeerLink.session()`,
C# `PeerLink.SessionAsync`). Split it, same shape in all three:

- **LinkManager** (new): owns the listening socket, the **active-link
  table** keyed by `node_id`, and the pre-routing gate.
  - Inbound: accept, exchange hellos, then run the full gate **before
    any routing**: AuthGate IP-block check and fail recording (moved
    here from per-PeerLink so failures aggregate per source IP across
    connections that never form a link), token check, version
    negotiation with major-mismatch refusal, self/duplicate-`node_id`
    drop. Only then route: hand the connection **and the parsed
    hello** (name, `node_id`, protocol minor) to the existing link for
    that `node_id`, or create one. The per-peer link never re-reads a
    hello; it receives it.
  - Outbound: for each discovered peer, ensure a link exists and kick
    its connect attempt. Discovery-side bookkeeping keeps its current
    owner and keying: Python `address_fails` + the retry loop, Swift
    `MdnsBeacon.addressFails`/`recordFail`/`pruneAddress` +
    `mdnsReconnectLoop`, C# `PeerDirectory` (`_knownPeers`,
    `_addressFails`) — all still **per address**, now feeding N links
    instead of one. `PeerDirectory` keeps the discovered-address
    table; LinkManager owns only the active-link table.
  - Broadcast: `send_clip(...)` fans out to every active link;
    per-link failures drop that link only.
- **PeerLink** (narrowed): exactly one peer pair — session lifecycle
  from the post-hello point, keepalive, per-link staleness watchdog,
  `peer_protocol_minor` (set from the handed-over hello), per-link
  send. The simultaneous-connect tie-break (lexicographic `node_id`)
  keeps its rule but is evaluated by LinkManager at routing time,
  since it is now a collision between two connections for the same
  `node_id`.

### Link lifecycle rules

- **Duplicate connection, healthy link:** a new authenticated
  connection for a `node_id` that already has a live session
  **replaces** that session (close the old socket, log). Rationale: a
  healthy peer never opens a second connection — a newcomer means the
  peer considers the old link dead (our side is half-open, e.g. after
  the peer slept). The tie-break applies only to the genuine
  simultaneous-connect race (both directions inside the handshake
  window); an established link is otherwise replaced, never defended.
- **`node_id` churn:** `node_id` is a fresh UUID every daemon start
  (`anyclip.py:2228`), so a peer restart arrives as a *new* `node_id`.
  When a link dies its table entry is removed immediately — dead links
  never count toward the cap and are never kept for reconnect
  (reconnect state lives in the per-address discovery bookkeeping, as
  today).
- **Cap:** `max_peers` active links (default 8; a shared constant in
  all three, plus a `--max-peers` CLI flag in the Python build only —
  `~/.anyclip/config.json` stays token-only). The cap is checked
  **after** `node_id` routing, so it only refuses hellos introducing a
  *new* `node_id`; a known peer reconnecting is routed, not refused.
  Over-cap: inbound refused after the gate with a log line; discovered
  peers beyond the cap are not dialed.
- **Re-admission:** when a link drops and frees a slot, LinkManager
  re-scans the discovery snapshot and dials the waiting peer; refused
  inbound peers also get in via their own retry/mDNS re-announce.
  Worst-case wait is one discovery/retry cycle — no new timer.

## Sync semantics

- Local clipboard change → broadcast to all active links. Detection is
  unchanged: the watcher and `EchoSuppressor` stay **global**
  (content-hash based), not per-link.
- Received clip → applied to the local clipboard only. **No relay.**
- **Receive applies are serialized** through one queue regardless of
  which link delivered them, and each apply marks the suppressor
  before touching the clipboard (the existing receive order). This is
  what keeps the single-slot-per-kind suppressor sufficient with N
  peers: the clipboard only ever holds the *last* applied clip, and at
  any watcher poll the suppressor slot holds that same clip's hash —
  an earlier clip is either already replaced (never polled) or polled
  while its hash still occupies the slot. No spurious re-broadcast
  window exists as long as apply order = mark order, which the serial
  queue guarantees.
- Two peers sending the same content near-simultaneously: second
  apply is a hash no-op. Different content near-simultaneously:
  last-writer-wins on the local clipboard, no ordering guarantees.
- Per-peer downgrade is preserved: the same multi-file clip goes out
  as `kind:"files"` to a minor ≥ 1 link and as the first-file legacy
  `kind:"file"` fallback to a minor 0 link (existing per-link gating,
  now evaluated per link in the broadcast loop).

## Watchdogs: per-link vs global

Two different mechanisms today; the split must keep them apart:

- **Per link:** the staleness dropper (Python
  `link_ping_loop`→`drop_stale_link` `anyclip.py:2048`, Swift
  `linkPingLoop`→`dropStaleLink`, C# equivalent) — pings and drops
  only its own half-open link. One sleeping peer drops only its link.
- **Global, unchanged scope:** the mDNS-health escalator (Python
  `idle_link_watchdog` `anyclip.py:2003`, Swift `idleLinkWatchdog` +
  `networkWatchdog`) — refreshes discovery and ultimately bounces the
  whole daemon. It must key on "**zero** links active", never on any
  single link's idleness; keyed per-link it would let one sleeping
  peer bounce the daemon and tear down every healthy link.

## Notifications & UI

This is an event-model change, not a display swap: `LinkUp`/`LinkDown`
events gain a stable peer identity (`node_id` + name — today
`LinkDown` carries only a reason: `peer_state.py:34`,
`PeerState.swift:7`, `PeerState.cs:6`), the UI state becomes a peer
collection keyed by `node_id` (today a single scalar `peer_name`), and
the reducers add/remove entries instead of collapsing to `searching`
on any `LinkDown`. The shells render the list: Python
`app/menubar_mac.py` + `app/tray_win.py`, Swift
`StatusItemController.swift`, C# tray in `AnyClipApp`. Toast **copy**
stays as today (per-peer link/unlink wording); skip/fallback
notifications for one local copy are aggregated into one toast across
all peers (same principle as the folder-skip aggregation in d8894a0).

Per implementation the touched surface is: Python `anyclip.py`
(PeerLink split) + `peer_state.py` + both app shells; Swift
`AnyClipDaemon` (`PeerLink.swift`, new `LinkManager.swift`,
`Daemon.swift` rewiring, `Watchdogs.swift`) + `AnyClipCore/
PeerState.swift` + `AnyClipApp/StatusItemController.swift`; C#
`AnyClipCore` (`PeerLink.cs`, new `LinkManager.cs`, `Daemon.cs`,
`PeerState.cs`, `Watchdogs.cs`) + `AnyClipApp` tray.

## Edge cases

- Reconnect backoff stays per address (discovery side); the per-link
  staleness dropper runs per link. A DHCP address change simply shows
  up as a new discovered address; the old one ages out through the
  existing failure pruning (no new bookkeeping).
- Self-advertisement filtering (mDNS TXT `id`) and pid-lock behavior
  are unchanged.

## Testing

- **Unit (all three):** LinkManager routing — new `node_id` creates a
  link, known `node_id` routes to it, duplicate connection replaces
  the live session, over-cap *new* peer refused while a known peer
  reconnect at cap is routed, dead link leaves the table (and frees
  the cap slot); broadcast fan-out with per-link failure isolation;
  per-link minor gating (one 1.1 + one 1.0 peer receive `"files"` vs
  first-file fallback from the same copy); suppressor under serialized
  applies — two peers delivering *different* clips back-to-back, then
  a watcher poll: assert nothing is re-broadcast; global escalator
  fires only at zero active links.
- **Interop (Swift + C#):** extend the harness to spawn **two**
  `fake_peer.py` instances against one daemon — both handshake, a
  local clip reaches both, and non-relay asserted deterministically:
  after peer A's clip is observed applied locally, drain peer B's
  socket for a bounded interval and assert zero clip frames arrived
  (this doubles as the echo-suppression-under-mesh check).
- **Python:** equivalent coverage in `tests/` (manager routing,
  broadcast, cap, replacement rule) with the existing asyncio
  patterns; `tests/test_peer_state.py` updated for the multi-peer
  state/reducer.
- Golden vectors: no regeneration (wire unchanged) — CI asserting
  byte-identical fixtures doubles as proof of that claim.
