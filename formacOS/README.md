# AnyClip for macOS (native Swift)

Swift port of the AnyClip client. Wire-compatible with the Python
implementation (protocol 1.0) and shares `~/.anyclip/` — an existing
token keeps working, and a Swift Mac syncs with a Python Windows peer.

## Build

Requires the Swift 6 CLI toolchain (Command Line Tools are enough; no
Xcode needed). Apple Silicon only.

```bash
swift test                  # unit + interop tests
Scripts/build-app.sh        # -> dist/AnyClip.app (ad-hoc signed)
ANYCLIP_BUILD_VERSION=1.1.0 Scripts/build-app.sh   # stamped release build
```

## Layout

- `Sources/AnyClipCore` — pure logic: wire codec, version negotiation,
  state reducer, config store, logging. No AppKit/Network imports.
- `Sources/AnyClipDaemon` — runtime: PeerLink (TCP + handshake),
  MdnsBeacon (Bonjour browse), ClipboardWatcher, watchdogs, daemon
  assembly with in-process supervisor.
- `Sources/AnyClipApp` — AppKit menu bar shell + onboarding.
- `Scripts/gen-golden-vectors.py` — regenerates the wire-protocol golden
  fixtures with Python encoding rules (commit the results).
- `Scripts/fake_peer.py` — stdlib-only wire-compatible peer used by the
  interop test.

## Token resolution

`ANYCLIP_TOKEN` env var > `~/.anyclip/config.json` > first-launch
onboarding dialog. Note: the Python CLI's `--token` flag has no Swift
equivalent — use the env var for ad-hoc token overrides.

## Not ported (deliberate)

Sparkle auto-update, `--headless` CLI mode, multi-file/folder sync.
See `../docs/superpowers/specs/2026-06-11-macos-native-port-design.md`.
