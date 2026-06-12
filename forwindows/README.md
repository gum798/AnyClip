# AnyClip for Windows (native C#)

C#/.NET 8 port of the AnyClip client. Wire-compatible with the Python and
Swift implementations (protocol 1.0) and shares `~/.anyclip/` — an
existing token keeps working across all three.

## Build & test

Core (platform-neutral) builds and tests anywhere, including macOS:

```bash
dotnet test tests/AnyClipCore.Tests     # incl. fake_peer.py interop
Scripts/publish-win.sh                  # cross-publish win-x64 single exe
```

The Windows layer (`src/AnyClipApp`, `tests/AnyClipApp.Tests`) builds
anywhere (`EnableWindowsTargeting`) but only runs on Windows — CI runs
those tests on `windows-latest`.

## Layout

- `src/AnyClipCore` — wire codec, PeerLink, watchdogs, daemon assembly
  behind `IClipboardSync`/`IMdnsService`/`IPidLock`.
- `src/AnyClipApp` — WinForms shell: WM_CLIPBOARDUPDATE watcher, dnsapi
  mDNS, NotifyIcon tray, dialogs, HKCU Run autostart.

## Manual smoke checklist (real Windows hardware)

1. Unzip `AnyClip-vX.Y.Z-windows-x64-native.zip`, run `AnyClip.exe`
   (SmartScreen: 추가 정보 → 실행).
2. First launch onboarding: enter the shared token from the other device.
3. Tray icon appears red while searching; turns normal when linked.
4. Copy text/image/file both ways with the Mac peer — all three sync;
   balloon notifications appear.
5. Token… menu: Enter token… / Reset… flows; Start at Login toggles the
   HKCU Run entry; Open Logs reveals `~/.anyclip/anyclip.log`.
6. Quit removes `~/.anyclip/anyclip.pid`; relaunching takes over an
   existing Python daemon (PID lock).

## Not ported (deliberate)

WinSparkle auto-update, `--headless`, multi-file/folder sync, permission
probe (no Local Network concept on Windows).
