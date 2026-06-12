# macOS native port (Swift) under ./formacOS

**Date:** 2026-06-11
**Status:** Approved

## Goal

Reimplement the AnyClip client as a native macOS app in Swift, living in
`formacOS/`, with **zero third-party dependencies**. It must be
wire-compatible with the existing Python implementation (protocol 1.0)
so a Swift Mac syncs with a Python Windows/Mac peer unchanged, and it
must share `~/.anyclip/` (config.json, anyclip.log, received/,
anyclip.pid) so an existing token keeps working.

## Scope

**In:** clipboard sync (text, image, file), mDNS discovery + advertising,
token auth, menu bar UI, first-launch onboarding, Start at Login,
desktop notifications, rotating file log, PID lock, self-healing
watchdogs, permission probe.

**Out (deliberate):** Sparkle auto-update, `--headless` CLI mode,
multi-file/folder sync (same scope-out as Python), Intel (arm64 only,
same as the Python build).

## Approach

SwiftPM package + AppKit, built with the CLI toolchain only (no Xcode
project — `xcodebuild` is unavailable on the dev machine). A build
script assembles `AnyClip.app` from the release binary, mirroring the
py2app philosophy of the Python build.

- **Networking:** Network.framework. `NWListener` (TCP listen +
  Bonjour advertise with TXT record), `NWBrowser` (discovery,
  `.bonjourWithTXTRecord`), `NWConnection` (outbound).
- **Concurrency:** Swift Concurrency. The Python asyncio task set maps
  1:1 onto actors + structured `Task`s.
- **Clipboard:** `NSPasteboard` polled on `changeCount` (cheaper than
  the Python re-read: content is only read when the count moves).
- **UI:** `NSStatusItem` menu bar + `NSAlert` onboarding (same UX as
  the PyObjC onboarding).

## Package layout

```
formacOS/
├── Package.swift                 # macOS 13+, three targets + tests
├── Sources/
│   ├── AnyClipCore/              # pure logic, no AppKit/Network imports
│   │   ├── WireProtocol.swift    # frame codec + message structs
│   │   ├── VersionNegotiator.swift
│   │   ├── PeerStateReducer.swift
│   │   ├── ConfigStore.swift
│   │   ├── EchoSuppressor.swift
│   │   ├── AuthGate.swift
│   │   └── Logging.swift         # rotating file logger
│   ├── AnyClipDaemon/            # runtime: network + clipboard + watchdogs
│   │   ├── PeerLink.swift
│   │   ├── MdnsBeacon.swift
│   │   ├── ClipboardWatcher.swift
│   │   ├── Watchdogs.swift
│   │   ├── PermissionProbe.swift
│   │   ├── PidLock.swift
│   │   └── Daemon.swift          # task assembly + in-process supervisor
│   └── AnyClipApp/               # AppKit shell (executable)
│       ├── main.swift
│       ├── AppDelegate.swift
│       ├── StatusItemController.swift
│       ├── Onboarding.swift
│       ├── Autostart.swift
│       └── Notifier.swift
├── Tests/AnyClipCoreTests/
├── Scripts/build-app.sh          # swift build -c release → dist/AnyClip.app
└── Resources/Info.plist.template
```

## Wire protocol (must match Python byte-for-byte semantics)

- Frame: 4-byte big-endian unsigned length N, then N bytes of UTF-8
  JSON. N == 0 or N > 16 MiB (`MAX_PAYLOAD`) ⇒ invalid, close link.
- `hello` (sent by both sides immediately on connect):
  `{"type":"hello","token":<sha256hex(token)>,"node_id":<uuid4 str>,
  "name":<display>,"version":1,"app_version":<str>,
  "protocol_major":1,"protocol_minor":0}` — the legacy integer
  `version` field must be present for old peers.
- `clip` text: `{"type":"clip","kind":"text","content":<str>,
  "hash":<sha256hex(content)>,"ts":<epoch float>}`.
- `clip` image: `kind:"image"`, `content` = base64 PNG, `hash` =
  sha256hex of raw bytes, plus `bytes:<count>`.
- `clip` file: `kind:"file"`, `name:<basename>`, `content` = base64,
  `hash`, `bytes`.
- `ping`/`pong`: `{"type":"ping","ts":...}`; ping is answered with pong;
  pong is consumed silently. Unknown types ignored.
- Handshake: send our hello, wait ≤ 5 s for peer hello; validate
  `type == "hello"`, token hash equality, version negotiation
  (major mismatch refuses the link; minor mismatch links with a log),
  `node_id != ours` (self-loopback drop). Base64 payloads are validated;
  bad payloads are logged and skipped without dropping the link.
- Tie-breaker (simultaneous connect): within a 1.5 s race window from
  the last `linkedAt`, keep this link iff
  `(outbound && ourId < peerId) || (inbound && ourId > peerId)`;
  after the window, a fresh authenticated handshake replaces the
  existing (presumed-zombie) link.
- AuthGate (inbound only): 5 consecutive handshake failures from one IP
  ⇒ 60 s cooldown; success clears; stale entries swept lazily.
- Outbound connect timeout 5 s; TCP keepalive enabled (idle 15 s).

## mDNS

- Service type `_anyclip._tcp`, instance name `{name}-{nodeId8}`.
- TXT: `id` (full node UUID), `version` ("1", legacy),
  `app_version`, `protocol_major` ("1"), `protocol_minor` ("0").
- Browse results whose TXT `id` equals our node id are ignored and do
  **not** count as mDNS evidence; resolving a non-self peer bumps
  `eventsSeen` (consumed by the permission probe).
- `knownPeers: [nodeId: endpoint]` feeds the reconnect loop;
  3 consecutive fast connect failures prune the address
  (`MAX_RECONNECT_FAILS = 3`); sessions that lasted > 5 s do not count
  as failures and reset backoff.
- `refresh()` re-advertises + re-issues the browse (used by the idle
  watchdog).

## Clipboard semantics (parity with Python)

- Poll every 0.5 s; skip all reads when `changeCount` is unchanged.
- Text: empty string updates the baseline but is never propagated
  (Screenshot.app clears the clipboard mid-capture).
- Image: convert to PNG; identical-hash skip; 1.0 s cooldown collapses
  the multi-representation flood after a screenshot; baseline seeded at
  startup so the pre-existing clipboard never fires a send.
- File: first file only; folders are skipped with a one-shot toast and
  fingerprint update (no retry loop); size budget
  `(16 MiB − 256 KiB) × 0.74`; fingerprint = (path, size, mtime ns) so
  bytes are read only on change; read failures also update the
  fingerprint.
- Inbound text/image: update the suppressor + watcher baseline *before*
  writing to NSPasteboard so the poller cannot echo.
- Inbound file: sanitize to basename, replace chars outside
  `[A-Za-z0-9._\- ]` with `_`, write under `~/.anyclip/received/`,
  update fingerprint, put a file-URL on the pasteboard.
- `~/.anyclip/received/` is emptied on startup and on graceful quit.

## State machine + menu bar

Pure reducer identical to `peer_state.reduce`: events PeerDiscovered /
LinkUp / LinkDown / HandshakeFailed / PermissionMissing fold into
idle / searching / linked(peer, since) / error(reason); 5 consecutive
handshake failures ⇒ error("auth"). Events flow over an AsyncStream
from the daemon to the AppKit shell.

Menu bar: static "@" title (state lives in the dropdown):

- `Status: Idle | Searching for peer | Linked: <peer> | Error: <reason>`
- `Linked since: HH:MM:SS`
- `Token…` — shows current token + path; Reset… double-confirms,
  saves a fresh token, then quits the app.
- `Start at Login` — toggle, checkmark reflects plist existence.
- `Open Logs` — reveals `~/.anyclip/anyclip.log` in Finder.
- `Open Local Network Settings` — only while error == local_network;
  opens `x-apple.systempreferences:com.apple.preference.security?Privacy_LocalNetwork`.
- `Quit` — graceful: close link, unregister mDNS, release PID file,
  clear received/.

No "Check for Updates" item (Sparkle is out of scope).

## Onboarding & config

Token resolution: `ANYCLIP_TOKEN` env > `~/.anyclip/config.json` >
onboarding dialog. Onboarding = NSAlert with three buttons (Generate
new token / Enter existing token / Cancel); the entry path uses an
accessory NSTextField; cancel exits the app. Generated tokens are
32 random bytes, base64url without padding (same shape as Python's
`secrets.token_urlsafe(32)`). Config writes are atomic
(temp file + fsync + rename) with 0600 permissions, JSON
`{"token": "..."}` — readable by the Python implementation and
vice versa.

## Process hygiene

- PID lock at `~/.anyclip/anyclip.pid` (`"<pid> <port>\n"`): on
  startup, terminate a previous anyclip (PID-file pid, then any
  listener on the port whose `ps` args contain "anyclip"
  case-insensitively — this also recognises the Python daemon);
  a foreign process on tcp/24816 is fatal with a clear message
  (NSAlert in GUI mode, then exit). SIGTERM → 2 s wait → SIGKILL.
- Rotating log at `~/.anyclip/anyclip.log`, 5 MB × 3 backups,
  line format `YYYY-MM-DD HH:MM:SS,mmm LEVEL message` (same shape as
  Python's logging so `Open Logs` shows one continuous history).
- In-process supervisor: the daemon run loop restarts on error with
  1 s → 60 s exponential backoff. (Improvement over the Python GUI
  build, whose DaemonSupervisor runs the daemon once — watchdog-raised
  restarts silently died there.)

## Watchdogs (parity)

- `linkPingLoop`: app-layer ping every 30 s while linked.
- `mdnsReconnectLoop`: while unlinked, retry knownPeers with
  1 → 60 s backoff, dedup by address, prune after 3 fast fails.
- `networkWatchdog`: every 15 s compare current primary IPv4
  (UDP-connect trick to 8.8.8.8:80) vs the advertised one; change ⇒
  restart the daemon (supervisor catches it).
- `idleLinkWatchdog`: every 60 s unlinked ⇒ `beacon.refresh()` up to
  3 times, then daemon restart.
- Permission probe (macOS): 30 s after start, 0 mDNS events + network
  present ⇒ `PermissionMissing(local_network)`; no network ⇒
  `no_network`.

## Autostart

LaunchAgent at `~/Library/LaunchAgents/com.anyclip.plist` with the
**same label** as the Python app (deliberate: prevents two autostart
entries fighting over port 24816 when a user migrates).
ProgramArguments = the .app's `Contents/MacOS/AnyClip` binary;
RunAtLoad + KeepAlive true; stdout/stderr to
`~/.anyclip/launchd.{stdout,stderr}.log`; `launchctl unload` (allowed
to fail) then `load` on enable, `unload` + delete on disable.

## Build & bundle

`Scripts/build-app.sh`:

1. `swift build -c release --arch arm64`
2. Assemble `formacOS/dist/AnyClip.app`:
   `Contents/MacOS/AnyClip` (binary), `Contents/Info.plist`
   (from template: `CFBundleIdentifier com.anyclip.AnyClip`,
   `LSUIElement true`, `NSLocalNetworkUsageDescription`,
   `NSBonjourServices [_anyclip._tcp]`, `CFBundleIconFile`,
   `CFBundleShortVersionString` from `ANYCLIP_BUILD_VERSION` env or
   `0.0.0-dev`), `Contents/Resources/anyclip.icns` (copied from
   `../app/icons/`).
3. Ad-hoc codesign (`codesign --force -s -`).

`APP_VERSION` is injected at build time into a generated
`Version.swift` (env `ANYCLIP_BUILD_VERSION`, default `0.0.0-dev`),
mirroring the Python CI convention.

Notifications use the UserNotifications framework; if authorization is
denied or the process is not running from a bundle, notification calls
become silent no-ops (a single log warning). The daemon never imports
UserNotifications: it exposes an injected `notify(title, body)` callback
(like Python's `notify_async`), wired by the AppKit shell to
`Notifier`. Toast titles/bodies match Python: `AnyClip ← <peer>` /
`AnyClip → <peer>`, 80-char single-line preview for text,
`image (N KB)`, `file: <name> (N KB)`, and the folder-skip message
`folder not synced — folders are not supported: <name>`.

## Testing

1. `swift test` (AnyClipCore):
   - Version negotiator: full table from `tests/test_version_negotiator.py`.
   - PeerStateReducer: golden transitions from `tests/test_peer_state.py`.
   - ConfigStore: load/save/corrupt/permissions round-trip, including a
     file written by the Python implementation.
   - Frame codec: **golden vectors generated by the Python code**
     (hello/clip/ping frames dumped to fixture files) decoded and
     re-encoded byte-compatibly (JSON key order may differ; semantic
     equality asserted on decode, length-prefix and UTF-8/base64
     handling asserted exactly).
   - EchoSuppressor, AuthGate (with injected clock).
2. Interop: a small Python fake-peer script (uses the real frame
   read/write logic, never touches the clipboard) listens on localhost;
   the Swift PeerLink connects, handshakes, exchanges text/image/file
   clips both ways; asserted from the test harness.
3. Manual: `Scripts/build-app.sh`, launch the .app, verify menu bar +
   onboarding.

## Risks / notes

- The Python `_is_anyclip_pid` check is case-sensitive ("anyclip"), so
  a *Python* instance starting while the Swift app holds the port will
  refuse to kill it and exit with the "non-anyclip process" error.
  Accepted: the migration direction is Python → Swift, and the Swift
  side recognises Python instances (case-insensitive check).
- Unsigned (ad-hoc) build keeps the existing Gatekeeper right-click
  bypass flow from the README.
- macOS 15+ shows the Local Network permission prompt on first mDNS
  use; the existing permission probe + menu shortcut handles refusal.

## Post-merge amendments (2026-06-11)

- Deployment target is macOS 14 (not 13) — matches Package.swift and
  Info.plist.
- APP_VERSION is read at runtime from CFBundleShortVersionString with an
  ANYCLIP_BUILD_VERSION env fallback (no generated Version.swift).
- The menu bar glyph is no longer always-static "@": it renders red while
  not linked and "@!" (red) on error, per user request. The dropdown
  structure is unchanged. The Token… alert gained an "Enter token…" flow.
- swift-testing is a test-only package dependency (CLT toolchains run
  zero tests with the bundled framework — see Package.swift comment);
  runtime targets remain dependency-free.
- Known follow-up: the tie-breaker race-window logic has no automated
  test (same gap exists in the Python original).
- Toast notifications are OFF by default and toggled via a new
  "Notifications" menu item (UserDefaults-backed, app-local); the default
  sync feedback is a 10-frame circular arc-orbit pulse of the menu bar
  glyph (accent-colored, 0.4 s, coalescing). The notification-permission
  prompt is deferred until the user first enables toasts.
