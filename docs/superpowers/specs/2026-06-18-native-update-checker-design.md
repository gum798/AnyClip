# Native version display + update check/install — design

**Status:** approved (design), pending spec review
**Date:** 2026-06-18
**Applies to:** `formacOS/` (Swift) and `forwindows/` (C#) native apps. Not the Python build (it already has Sparkle/WinSparkle).

## Goal

1. Show the running app version in the menu bar (macOS) and tray menu (Windows).
2. Add a **Check for Updates** action that detects a newer release and installs it **through the package manager the app was distributed with** — `brew upgrade --cask anyclip` on macOS, `scoop update anyclip` on Windows — then relaunches.

## Non-goals (YAGNI)

- Background polling / scheduled checks (only: one manual check + one silent best-effort check on launch).
- In-app changelog/release-notes display.
- Sparkle-style self-download-and-replace of the bundle (deliberately avoided by the native ports; we defer to brew/scoop).
- Detecting *which* manager installed the app — we attempt the platform's documented manager and fall back to the browser on any failure.

## Architecture

Three layers, mirrored in both implementations, following the existing `core → daemon/runtime → GUI shell` split.

### Layer 1 — pure logic (`AnyClipCore`, unit-tested, no IO)

`UpdateChecker` — pure functions + an async check that takes an **injected fetcher** so tests never hit the network.

- `parseLatestTag(githubJSON) -> String?`
  Extract `tag_name` from the GitHub `releases/latest` JSON, strip a leading `v`. Returns nil on malformed input.
- `compareVersions(_ a: String, _ b: String) -> Order` (`.ascending`/`.same`/`.descending`)
  Semver-ish: split the numeric core on `.`, compare component-by-component as integers (so `1.1.10 > 1.1.9`). A version carrying a pre-release suffix (`1.1.8-beta`) ranks **below** the same core without one. A version that does not parse as numeric (e.g. `0.0.0-dev`, empty) ranks **lowest** — a dev/unknown build therefore always sees a release as "newer", which is harmless.
- `checkForUpdate(current: String, fetch: () async throws -> String) async -> UpdateStatus`
  `UpdateStatus = .upToDate(current) | .available(latest: String, url: String) | .failed(reason: String)`.
  Calls `fetch` (real impl does the HTTPS GET), parses, compares; any thrown error or unparseable body → `.failed`.

Constants (core):
- `releasesApiURL = "https://api.github.com/repos/gum798/AnyClip/releases/latest"`
- `releasesPageURL = "https://github.com/gum798/AnyClip/releases/latest"`

`UpdateCommand` — pure builder for the install/relaunch helper invocation (testable; see Layer 2).

### Layer 2 — runtime (platform IO)

`UpdateService` (macOS: `AnyClipDaemon`/app glue; Windows: `AnyClipApp`):

- **Fetch:** real `fetch` closure. macOS `URLSession`, Windows `HttpClient`.
  Headers: `User-Agent: AnyClip-updater` (GitHub 403s without a UA) and `Accept: application/vnd.github+json`. ~8s timeout. Non-200 → throw → `.failed`.
- **Install (detached helper pattern, both platforms):** the running app cannot reliably replace itself (Windows locks the running `.exe`; on macOS we avoid replace-while-running too). On "Update now" the app:
  1. spawns a **detached helper** that: waits for this PID to exit → runs the package-manager update → relaunches the app → on update failure, opens `releasesPageURL`;
  2. then quits gracefully.

  macOS helper (`/bin/sh -c`, using absolute tool paths):
  ```
  while kill -0 <PID> 2>/dev/null; do sleep 0.3; done
  <BREW> upgrade --cask anyclip && /usr/bin/open -a AnyClip || /usr/bin/open <releasesPageURL>
  ```
  `<BREW>` resolved in priority order: `/opt/homebrew/bin/brew`, `/usr/local/bin/brew`, else `brew` on PATH.

  Windows helper (`powershell -WindowStyle Hidden -Command`):
  ```
  Wait-Process -Id <PID> -ErrorAction SilentlyContinue
  try { scoop update anyclip; Start-Process '<exePath>' }
  catch { Start-Process '<releasesPageURL>' }
  ```
  `scoop` resolved via `%USERPROFILE%\scoop\shims\scoop.cmd` if present, else `scoop` on PATH.

  The argument list / script string is produced by a **pure `UpdateCommand` builder** (PID, tool path, app path, URL in → argv/script out) so it is unit-tested without spawning anything.

- **Fallback open-in-browser** is also used directly when a *check* fails (`.failed`) or when the user picks "Open releases".

### Layer 3 — GUI shell

macOS `StatusItemController`, Windows `TrayIcon`:

- **Version line:** a disabled item `AnyClip v<version>` at the top of the menu.
- **Check for Updates item.** States:
  - idle → `Check for Updates`
  - in-flight → `Checking…` (disabled)
  - `.upToDate` → `You're up to date (v<cur>)` for ~3s, then back to idle
  - `.available(v)` → actionable `Update to v<v>` → on click: confirm dialog ("AnyClip will close, update via Homebrew/Scoop, and reopen.") → run install (Layer 2) → quit
  - `.failed` → `Check failed — open releases` (click opens `releasesPageURL`)
- **Silent launch check:** one best-effort `checkForUpdate` on startup; on `.available` set the item label to `Update to v<v>` and the version line gets an "update available" hint. **No popup, no notification** (respects the opt-in-notifications preference). Silent on failure/offline.

## Data flow

```
launch ──► UpdateService.fetch ──► UpdateChecker.checkForUpdate ──► .available? set menu label (silent)
menu "Check for Updates" ──► UpdateService.fetch ──► checkForUpdate ──► render state in menu
menu "Update to vX" ──► confirm ──► spawn detached helper (UpdateCommand) ──► quit ──► helper: wait → brew/scoop → relaunch (│ fail → open releases)
```

## Error handling

Never crash. Every failure path degrades to opening `releasesPageURL` (manual install) and/or a `Check failed` label:
- offline / timeout / non-200 / GitHub rate-limit (403) → `.failed`
- malformed JSON / missing `tag_name` → `.failed`
- brew/scoop missing or non-zero exit → helper opens the releases page

## Testing

Pure-core unit tests (both Swift + C#, mirrored):
- `compareVersions`: older/same/newer; multi-digit (`1.1.10` vs `1.1.9`); pre-release lower than release; `0.0.0-dev`/empty rank lowest.
- `parseLatestTag`: real-shaped GitHub JSON → `1.1.7`; `v`-strip; malformed → nil.
- `checkForUpdate` with a fake fetcher: newer → `.available` (correct version+url); same → `.upToDate`; throwing fetcher → `.failed`; malformed body → `.failed`.
- `UpdateCommand` builder: correct argv/script for given PID/tool/app/url (both platforms' shapes).

Manual/uncovered by automation: the actual subprocess spawn + relaunch (platform IO) — verified by hand on each OS.

## File-by-file change list

**macOS (`formacOS/`)**
- `Sources/AnyClipCore/UpdateChecker.swift` *(new)* — `UpdateChecker`, `UpdateStatus`, `compareVersions`, `parseLatestTag`, URLs.
- `Sources/AnyClipCore/UpdateCommand.swift` *(new)* — pure helper-script builder.
- `Sources/AnyClipApp/UpdateService.swift` *(new)* — URLSession fetch + detached-helper spawn + browser fallback.
- `Sources/AnyClipApp/StatusItemController.swift` — version line + Check-for-Updates item + states.
- `Sources/AnyClipApp/AppDelegate.swift` — pass version, wire UpdateService, launch silent check.
- `Tests/AnyClipCoreTests/UpdateCheckerTests.swift` *(new)*, `UpdateCommandTests.swift` *(new)*.

**Windows (`forwindows/`)**
- `src/AnyClipCore/UpdateChecker.cs` *(new)* — same pure logic as Swift.
- `src/AnyClipCore/UpdateCommand.cs` *(new)* — pure PowerShell-helper builder.
- `src/AnyClipApp/UpdateService.cs` *(new)* — HttpClient fetch + detached helper + browser fallback.
- `src/AnyClipApp/TrayIcon.cs` — version header + Check-for-Updates item + states.
- `tests/AnyClipCore.Tests/UpdateCheckerTests.cs` *(new)*, `UpdateCommandTests.cs` *(new)*.

Wire protocol is **untouched** — this feature is local to each app; no golden-vector/interop impact.
