# Windows native port (C# / .NET 8) under ./forwindows

**Date:** 2026-06-12
**Status:** Approved

## Goal

Reimplement the AnyClip client as a native Windows app in C# (.NET 8),
living in `forwindows/`, with **zero third-party runtime dependencies**.
Wire-compatible with the Python and Swift implementations (protocol 1.0)
and shares `~/.anyclip/` (config.json, anyclip.log, received/,
anyclip.pid) so an existing token keeps working across all three.

The behavioral reference is the Python implementation (`anyclip.py`,
`app/tray_win.py`, `app/onboarding_win.py`, `autostart.py`); the
structural reference is the completed Swift port
(`docs/superpowers/specs/2026-06-11-macos-native-port-design.md`) —
wire protocol, clipboard semantics, state machine, watchdogs, and
notification strings are identical unless this spec says otherwise.

## Scope

**In:** clipboard sync (text, image, file), mDNS discovery + advertising,
token auth, tray UI (incl. the 2026-06-11 UI additions: enter-token flow
and not-linked-attention icon), first-launch onboarding, Start at Login
(HKCU Run key), balloon notifications, rotating file log, PID lock,
self-healing watchdogs.

**Out (deliberate):** WinSparkle auto-update, `--headless` CLI mode,
multi-file/folder sync, permission probe (Windows has no Local Network
permission — the OS firewall popup covers it; same as Python, which only
wires the probe on darwin), ARM64 Windows (x64 only, same as the Python
build).

## Development constraint (drives the architecture)

The dev machine is macOS: Windows binaries cannot run locally.
Mitigation — maximize the platform-neutral surface:

- .NET sockets (`Socket`/`TcpListener`) are cross-platform, so unlike
  the Swift port, **PeerLink, framing, watchdogs, and the daemon
  assembly live in the neutral Core** and are built, unit-tested, and
  interop-tested (against `formacOS/Scripts/fake_peer.py`) ON macOS.
- Only the clipboard, mDNS, PID/process, tray, and dialogs are
  Windows-bound; those are exercised by xunit on the CI
  `windows-latest` runner and manually smoke-tested on the user's
  Windows machine.
- Cross-build from macOS with `-r win-x64` +
  `-p:EnableWindowsTargeting=true` (WinForms cross-targeting).

## Solution layout

```
forwindows/
├── AnyClip.sln
├── src/
│   ├── AnyClipCore/                 # net8.0 (platform-neutral)
│   │   ├── Wire.cs                  # constants (mirror anyclip.py)
│   │   ├── WireMessage.cs           # JSON codec, snake_case wire fields
│   │   ├── VersionNegotiator.cs
│   │   ├── PeerStateReducer.cs      # + TrayIconSpec mapping (linked/plain,
│   │   │                            #   unlinked/attention, error/attention+bang)
│   │   ├── ConfigStore.cs           # shared ~/.anyclip/config.json
│   │   ├── EchoSuppressor.cs
│   │   ├── AuthGate.cs              # sweep-on-recordFail (Swift-port lesson)
│   │   ├── TxtCodec.cs              # DNS TXT key=value codec
│   │   ├── RotatingLog.cs           # 5 MB × 3, Python line format
│   │   ├── FramedConnection.cs      # 4-byte BE + JSON over Socket
│   │   ├── PeerLink.cs              # handshake, tie-break, receive loop
│   │   ├── Watchdogs.cs             # ping/reconnect/idle/network loops
│   │   └── Daemon.cs                # assembly + in-process supervisor
│   └── AnyClipApp/                  # net8.0-windows, WinForms, OutputType WinExe
│       ├── Program.cs               # [STAThread] entry, token resolution
│       ├── ClipboardWatcher.cs      # WM_CLIPBOARDUPDATE listener window
│       ├── MdnsBeacon.cs            # P/Invoke DnsServiceRegister/Browse
│       ├── PidLock.cs               # pid-file trust + Process.Kill
│       ├── TrayIcon.cs              # NotifyIcon + ContextMenuStrip
│       ├── Dialogs.cs               # onboarding, token show/enter/reset
│       ├── Autostart.cs             # HKCU Run key
│       └── Notifier.cs              # NotifyIcon.ShowBalloonTip
├── tests/
│   ├── AnyClipCore.Tests/           # xunit, net8.0 — runs on macOS
│   └── AnyClipApp.Tests/            # xunit, net8.0-windows — CI only
└── Scripts/
    └── publish-win.sh               # dotnet publish single-file → zip
```

Concurrency: async/await `Task`s map the asyncio task set 1:1. The
registration critical section in PeerLink uses a `SemaphoreSlim(1,1)`
(C# has no actor isolation; the lock mirrors Python's `asyncio.Lock`
directly).

## Wire protocol, state machine, constants

Byte-identical to the macOS spec (which matched `anyclip.py`):
4-byte big-endian length + UTF-8 JSON frames; hello with legacy
`version: 1`; clip text/image/file with base64 + sha256 hex hashes;
ping/pong; 5 s handshake timeout; 1.5 s tie-break race window with the
same keep rule; AuthGate 5 fails/60 s (with the sweep-before-count fix
from the Swift port); MAX_PAYLOAD 16 MiB; port 24816; reconnect prune
at 3 fast fails with 1→60 s backoff; link ping 30 s; idle watchdog
60 s × 3 refresh then daemon restart; IPv4-change watchdog 15 s;
in-process supervisor restart 1→60 s with FatalStartupError stopping.
JSON via System.Text.Json with snake_case property names spelled
explicitly (`[JsonPropertyName]`), nulls omitted, unknown fields
ignored. Golden vectors are REUSED from
`formacOS/Tests/AnyClipCoreTests/Fixtures/` (single source of truth);
the test project references them by relative path.

EADDRINUSE lesson from the Swift live test is designed in from day
one: PidLock settles 0.3 s after terminating a predecessor, and the
listener bind retries 4 × 0.5 s before raising FatalStartupError.

## Clipboard (Windows-specific)

- **Event-driven, not polled**: a hidden message-only window calls
  `AddClipboardFormatListener` and receives `WM_CLIPBOARDUPDATE`
  (the Windows-native equivalent of the macOS changeCount gate; the
  Python version polls every 0.5 s — same semantics, better trigger).
- All Clipboard reads/writes happen on the WinForms UI thread (STA
  requirement) via `Control.Invoke`; payload processing and network
  I/O stay off it.
- Text: `Clipboard.GetText()` / `SetText`. Empty text updates the
  baseline but is never propagated.
- Image: `Clipboard.GetImage()` → PNG encode (System.Drawing);
  sha256 baseline + 1.0 s cooldown absorbs multi-format floods
  (Windows fires WM_CLIPBOARDUPDATE once per copy, but defensive
  parity is kept). Inbound: PNG bytes → Bitmap → `SetImage`.
- File: `Clipboard.GetFileDropList()` first entry only; folders
  skipped with one toast + fingerprint update; size budget
  `(16 MiB − 256 KiB) × 0.74`; fingerprint (path, size, mtime).
  Inbound: sanitize basename, write under `~/.anyclip/received/`,
  `SetFileDropList` with the single path.
- Echo protection: baselines updated BEFORE writes + EchoSuppressor
  at the daemon layer, identical ordering to the other ports.
- `received/` cleared on startup and graceful quit.

## mDNS (Windows-specific — highest-risk component)

Windows 10 1809+ ships mDNS in `dnsapi.dll`:

- Advertise: `DnsServiceConstructInstance` + `DnsServiceRegister`
  (instance `{name}-{nodeId8}._anyclip._tcp.local`, port, TXT
  id/version/app_version/protocol_major/protocol_minor);
  `DnsServiceDeRegister` on quit; re-register on `refresh()`.
- Browse: `DnsServiceBrowse` for `_anyclip._tcp.local`; callbacks
  deliver instance + TXT + host/port; self-id filtered without
  counting (eventsSeen bookkeeping kept for parity even though no
  permission probe consumes it on Windows — it feeds debug logging).
- All P/Invoke signatures isolated in one internal `DnsApi` class.
- **Contingency (decided now, so a failure does not stall the
  project):** if DnsService APIs prove unreliable on CI/real hardware,
  replace MdnsBeacon's internals with a minimal managed mDNS
  responder/querier on UDP 5353 multicast (PTR/SRV/TXT/A answer and
  query; TxtCodec already exists). The MdnsBeacon public surface
  (start/refresh/stop/ingest bookkeeping) stays unchanged either way.

## Process hygiene (Windows-specific)

- PID lock: same `~/.anyclip/anyclip.pid` (`"<pid> <port>\n"`).
  Windows semantics follow Python's: the pid-file evidence is trusted
  (Python's `_is_anyclip_pid` returns True on win32; no lsof port
  probe). Terminate via `Process.GetProcessById` + `Kill()` with the
  same 2 s wait, then 0.3 s socket settle. A bind failure after
  cleanup raises FatalStartupError (after the 4×0.5 s retry).
- Rotating log: same file, same `yyyy-MM-dd HH:mm:ss,fff LEVEL msg`
  line shape, 5 MB × 3 backups.
- Quit: tray Quit → cancel daemon → cleanup (link close, mDNS
  deregister, pid release, received/ clear) with 3 s deadline →
  `Application.Exit()`. External taskkill skips cleanup; the stale
  pid self-heals on next start (accepted, same as the other ports).

## Tray UI

NotifyIcon + ContextMenuStrip, mirroring `tray_win.py` plus the
2026-06-11 UI parity items:

- **Icon states** (maps `TrayIconSpec` in Core): linked → normal
  anyclip.ico; not linked (idle/searching) → red-tinted variant
  (generated at runtime from the base icon via System.Drawing);
  error → red variant + "!" overlay. Tooltip: `AnyClip — <status>`.
- Menu: `Status: …` (disabled), `Linked since: …` (disabled),
  separator, `Token…`, `Start at Login` (checked = Run key exists),
  `Open Logs` (explorer /select), separator, `Quit`.
  No "Check for Updates".
- `Token…` dialog (WinForms Form, replacing MessageBoxW chains):
  shows current token + path with three buttons — Close (default),
  Enter token… (paste field, trimmed, empty = cancel), Reset…
  (confirm → generate). Both mutating paths save via ConfigStore
  (surfacing save failures), show "quit & relaunch to apply", quit.
- Onboarding (first launch, no env/config token): WinForms dialog
  with Generate / Enter existing / Cancel — same three-way flow as
  `onboarding_win.py`'s tkinter dialog.
- Notifications: `NotifyIcon.ShowBalloonTip` with the exact strings
  of the other ports (`AnyClip ← <peer>` / `AnyClip → <peer>` /
  preview / `image (N KB)` / `file: <name> (N KB)` / folder-skip).
- Token resolution order: `ANYCLIP_TOKEN` env > config.json >
  onboarding dialog.

## Autostart

HKCU `Software\Microsoft\Windows\CurrentVersion\Run`, value name
`AnyClip` (same as Python — prevents duplicate entries when a user
migrates). Command = quoted path to the installed `AnyClip.exe`
(Python's `format_windows_command` quoting rules). Enable/disable via
Microsoft.Win32.Registry; the menu checkmark reflects the value's
existence and is only set on successful write.

## Build, packaging, CI

- Local (macOS): `dotnet build` + `dotnet test tests/AnyClipCore.Tests`
  run natively; the app cross-builds with
  `dotnet publish -r win-x64 -p:EnableWindowsTargeting=true`.
- `Scripts/publish-win.sh`: single-file self-contained publish
  (`-p:PublishSingleFile=true`, `IncludeNativeLibrariesForSelfExtract`)
  → `AnyClip-v<ver>-windows-x64-native.zip` (named to coexist with the
  legacy PyInstaller `AnyClip-v<ver>-windows-x64.zip`).
- Version: CI passes `-p:InformationalVersion=$ANYCLIP_BUILD_VERSION`;
  runtime reads the assembly attribute with `0.0.0-dev` fallback.
- `release.yml` gains a `windows-native` job (windows-latest):
  `dotnet test` (both test projects) → publish → zip → upload to the
  release. The existing `homebrew` job is untouched (cask is
  macOS-only).

## Testing

1. **On macOS (local)**: xunit over AnyClipCore — golden vectors from
   the shared formacOS fixtures, version negotiator table, reducer +
   TrayIconSpec, ConfigStore (incl. Python-written file), AuthGate
   (incl. stale-count regression), TxtCodec, RotatingLog, framing
   loopback, PeerLink two-link handshake/auth-reject/version-refuse/
   ping-pong + bind-retry, Daemon start/cancel/pid-release, and the
   fake_peer.py interop test.
2. **On CI (windows-latest)**: the same Core suite plus
   AnyClipApp.Tests — Autostart against a temp registry key
   (`HKCU\Software\AnyClipTest…`), PidLock with throwaway processes,
   ClipboardWatcher logic with an injected clipboard abstraction
   (real Clipboard smoke kept minimal — CI clipboards are flaky),
   TrayIconSpec rendering smoke.
3. **Manual smoke (user's Windows machine)**: install zip, onboard
   with the shared token, verify tray states, bidirectional sync with
   the Mac (Swift) peer, Start at Login, Quit cleanliness. Checklist
   shipped in `forwindows/README.md`.

## Risks

- **DnsService P/Invoke** is the long pole; contingency documented
  above. CI cannot fully validate multicast behavior — the manual
  smoke test on real hardware is the acceptance gate for discovery.
- WinForms cross-targeting from macOS builds but never runs locally;
  any UI-thread bug surfaces only on CI/manual testing.
- Self-contained single-file exe is ~70–90 MB (accepted; framework-
  dependent builds would require .NET install on the target).
- Windows Defender SmartScreen on an unsigned exe: same
  "추가 정보 → 실행" flow as the existing Python zip (README already
  documents it).
