# Native version display + update check/install — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show the app version and add a package-manager-backed "Check for Updates" (brew/scoop + relaunch) to the native macOS and Windows apps.

**Architecture:** Pure `UpdateChecker` + `UpdateCommand` in each `AnyClipCore` (injected network fetcher, unit-tested). A platform `UpdateService` does the real HTTPS GET and spawns a detached helper that waits for the app to exit, runs the package-manager upgrade, and relaunches (browser fallback on failure). The menu/tray shells get a version line and a stateful "Check for Updates" item.

**Tech Stack:** Swift 6 / AppKit / URLSession (macOS); C# .NET 8 / WinForms / HttpClient (Windows). Tests: swift-testing, xunit.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-06-18-native-update-checker-design.md`.
- Release repo / endpoints (verbatim): API `https://api.github.com/repos/gum798/AnyClip/releases/latest`; page `https://github.com/gum798/AnyClip/releases/latest`.
- Package names (verbatim): Homebrew cask `anyclip`; Scoop app `anyclip`.
- GitHub API requires a `User-Agent` header (else 403) plus `Accept: application/vnd.github+json`; ~8s timeout; non-200 → failure.
- Version compare rule: numeric components split on `.` (so `1.1.10 > 1.1.9`); a pre-release (`-` suffix) ranks below the same core; unparseable/`0.0.0-dev` ranks lowest.
- Wire protocol is untouched — no golden-vector/interop changes.
- No new popups/toasts on the silent launch check (respects opt-in notifications).
- Commit trailer on every commit: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.
- Run from repo root `/Users/seojeonghwa/project/AnyClip`; Swift uses `--package-path formacOS`; dotnet is at `$HOME/.dotnet/dotnet` (add to PATH).

---

### Task 1: Swift core — `UpdateChecker`

**Files:**
- Create: `formacOS/Sources/AnyClipCore/UpdateChecker.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/UpdateCheckerTests.swift`

**Interfaces:**
- Produces: `enum UpdateStatus: Equatable, Sendable { case upToDate(current: String); case available(latest: String, url: String); case failed(reason: String) }`; `enum UpdateChecker` with `static let releasesApiURL/releasesPageURL: String`, `static func parseLatestTag(_:) -> String?`, `static func compareVersions(_:_:) -> ComparisonResult`, `static func checkForUpdate(current:fetch:) async -> UpdateStatus`.

- [ ] **Step 1: Write the failing test**

```swift
// formacOS/Tests/AnyClipCoreTests/UpdateCheckerTests.swift
import Testing
import Foundation
@testable import AnyClipCore

@Test func compareVersionsOrders() {
    #expect(UpdateChecker.compareVersions("1.1.6", "1.1.7") == .orderedAscending)
    #expect(UpdateChecker.compareVersions("1.1.7", "1.1.7") == .orderedSame)
    #expect(UpdateChecker.compareVersions("1.2.0", "1.1.9") == .orderedDescending)
    #expect(UpdateChecker.compareVersions("1.1.10", "1.1.9") == .orderedDescending) // numeric, not lexical
    #expect(UpdateChecker.compareVersions("1.1.8-beta", "1.1.8") == .orderedAscending) // pre-release lower
    #expect(UpdateChecker.compareVersions("0.0.0-dev", "1.1.7") == .orderedAscending) // dev lowest
}

@Test func parseLatestTagStripsV() {
    #expect(UpdateChecker.parseLatestTag(#"{"tag_name":"v1.1.7","name":"x"}"#) == "1.1.7")
    #expect(UpdateChecker.parseLatestTag(#"{"tag_name":"1.2.0"}"#) == "1.2.0")
    #expect(UpdateChecker.parseLatestTag(#"{"no_tag":true}"#) == nil)
    #expect(UpdateChecker.parseLatestTag("not json") == nil)
}

@Test func checkForUpdateClassifies() async {
    let newer = await UpdateChecker.checkForUpdate(current: "1.1.6") { #"{"tag_name":"v1.1.7"}"# }
    #expect(newer == .available(latest: "1.1.7", url: UpdateChecker.releasesPageURL))
    let same = await UpdateChecker.checkForUpdate(current: "1.1.7") { #"{"tag_name":"v1.1.7"}"# }
    #expect(same == .upToDate(current: "1.1.7"))
    let bad = await UpdateChecker.checkForUpdate(current: "1.1.7") { "garbage" }
    if case .failed = bad {} else { Issue.record("expected .failed for unparseable body") }
    struct Boom: Error {}
    let threw = await UpdateChecker.checkForUpdate(current: "1.1.7") { throw Boom() }
    if case .failed = threw {} else { Issue.record("expected .failed when fetch throws") }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `swift test --package-path formacOS --filter UpdateCheckerTests`
Expected: FAIL (cannot find `UpdateChecker` in scope).

- [ ] **Step 3: Write minimal implementation**

```swift
// formacOS/Sources/AnyClipCore/UpdateChecker.swift
import Foundation

public enum UpdateStatus: Equatable, Sendable {
    case upToDate(current: String)
    case available(latest: String, url: String)
    case failed(reason: String)
}

/// Pure update detection. Network IO is injected via `fetch`, so the
/// parse/compare logic is unit-testable without hitting GitHub.
public enum UpdateChecker {
    public static let releasesApiURL =
        "https://api.github.com/repos/gum798/AnyClip/releases/latest"
    public static let releasesPageURL =
        "https://github.com/gum798/AnyClip/releases/latest"

    /// `tag_name` from GitHub releases JSON, leading "v" stripped. nil if absent/malformed.
    public static func parseLatestTag(_ json: String) -> String? {
        guard let data = json.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String else { return nil }
        let v = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
        return v.isEmpty ? nil : v
    }

    /// Semver-ish: numeric components dominate; a pre-release ("-" suffix)
    /// ranks below the same core; non-numeric components sort low.
    public static func compareVersions(_ a: String, _ b: String) -> ComparisonResult {
        let pa = parse(a), pb = parse(b)
        let n = max(pa.nums.count, pb.nums.count)
        for i in 0..<n {
            let x = i < pa.nums.count ? pa.nums[i] : 0
            let y = i < pb.nums.count ? pb.nums[i] : 0
            if x != y { return x < y ? .orderedAscending : .orderedDescending }
        }
        if pa.isPre != pb.isPre { return pa.isPre ? .orderedAscending : .orderedDescending }
        return .orderedSame
    }

    private static func parse(_ v: String) -> (nums: [Int], isPre: Bool) {
        let core = v.split(separator: "-", maxSplits: 1).first.map(String.init) ?? v
        let nums = core.split(separator: ".").map { Int($0) ?? -1 }
        return (nums, v.contains("-"))
    }

    public static func checkForUpdate(
        current: String, fetch: () async throws -> String
    ) async -> UpdateStatus {
        let body: String
        do { body = try await fetch() }
        catch { return .failed(reason: "\(error)") }
        guard let latest = parseLatestTag(body) else {
            return .failed(reason: "could not parse latest release")
        }
        return compareVersions(current, latest) == .orderedAscending
            ? .available(latest: latest, url: releasesPageURL)
            : .upToDate(current: current)
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `swift test --package-path formacOS --filter UpdateCheckerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add formacOS/Sources/AnyClipCore/UpdateChecker.swift formacOS/Tests/AnyClipCoreTests/UpdateCheckerTests.swift
git commit -m "feat(macos): pure UpdateChecker (parse/compare/check)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Swift core — `UpdateCommand`

**Files:**
- Create: `formacOS/Sources/AnyClipCore/UpdateCommand.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/UpdateCommandTests.swift`

**Interfaces:**
- Produces: `enum UpdateCommand { static func macHelperScript(pid:brewPath:appName:releasesURL:) -> String }`.

- [ ] **Step 1: Write the failing test**

```swift
// formacOS/Tests/AnyClipCoreTests/UpdateCommandTests.swift
import Testing
@testable import AnyClipCore

@Test func macHelperScriptHasAllPieces() {
    let s = UpdateCommand.macHelperScript(
        pid: 4242, brewPath: "/opt/homebrew/bin/brew",
        appName: "AnyClip", releasesURL: "https://example.test/r")
    #expect(s.contains("kill -0 4242"))                        // waits for our exit
    #expect(s.contains("/opt/homebrew/bin/brew upgrade --cask anyclip"))
    #expect(s.contains(#"/usr/bin/open -a "AnyClip""#))        // relaunch on success
    #expect(s.contains(#"/usr/bin/open "https://example.test/r""#)) // fallback on failure
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `swift test --package-path formacOS --filter UpdateCommandTests`
Expected: FAIL (cannot find `UpdateCommand`).

- [ ] **Step 3: Write minimal implementation**

```swift
// formacOS/Sources/AnyClipCore/UpdateCommand.swift

/// Pure builders for the detached update helper invocation (kept out of the
/// runtime so the exact command is unit-testable without spawning anything).
public enum UpdateCommand {
    /// `/bin/sh -c` script: wait for `pid` to exit, run the cask upgrade,
    /// relaunch the app; on upgrade failure open the releases page instead.
    public static func macHelperScript(
        pid: Int32, brewPath: String, appName: String, releasesURL: String
    ) -> String {
        """
        while kill -0 \(pid) 2>/dev/null; do sleep 0.3; done
        if \(brewPath) upgrade --cask anyclip; then /usr/bin/open -a "\(appName)"; else /usr/bin/open "\(releasesURL)"; fi
        """
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `swift test --package-path formacOS --filter UpdateCommandTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add formacOS/Sources/AnyClipCore/UpdateCommand.swift formacOS/Tests/AnyClipCoreTests/UpdateCommandTests.swift
git commit -m "feat(macos): pure UpdateCommand helper-script builder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Swift runtime + UI — `UpdateService`, version line, Check-for-Updates

**Files:**
- Create: `formacOS/Sources/AnyClipApp/UpdateService.swift`
- Modify: `formacOS/Sources/AnyClipApp/StatusItemController.swift`
- Modify: `formacOS/Sources/AnyClipApp/AppDelegate.swift`

**Interfaces:**
- Consumes: `UpdateChecker`, `UpdateCommand`, `UpdateStatus` (Task 1–2).
- Produces: `@MainActor final class UpdateService` with `init(appVersion:)`, `func check() async -> UpdateStatus`, `func installAndRelaunch()`, `func openReleasesPage()`. `StatusItemController.init` gains `appVersion:`, `onCheckUpdates: @escaping () async -> UpdateStatus`, `onInstallUpdate: @escaping () -> Void`, `onOpenReleases: @escaping () -> Void`; plus `func runSilentUpdateCheck()`.

- [ ] **Step 1: Create `UpdateService.swift`**

```swift
// formacOS/Sources/AnyClipApp/UpdateService.swift
import Foundation
import AppKit
import AnyClipCore

/// Real network + process side of updates. Keeps StatusItemController free of IO.
@MainActor
final class UpdateService {
    private let appVersion: String
    init(appVersion: String) { self.appVersion = appVersion }

    func check() async -> UpdateStatus {
        await UpdateChecker.checkForUpdate(current: appVersion) {
            try await Self.fetchLatestJSON()
        }
    }

    private static func fetchLatestJSON() async throws -> String {
        var req = URLRequest(url: URL(string: UpdateChecker.releasesApiURL)!)
        req.setValue("AnyClip-updater", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 8
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
            throw NSError(domain: "AnyClip", code: 1,
                userInfo: [NSLocalizedDescriptionKey: "GitHub returned a non-200 status"])
        }
        return String(decoding: data, as: UTF8.self)
    }

    /// Spawn the detached upgrade helper, then the caller quits. The helper
    /// outlives us (reparented to launchd) and relaunches the new app.
    func installAndRelaunch() {
        let script = UpdateCommand.macHelperScript(
            pid: ProcessInfo.processInfo.processIdentifier,
            brewPath: Self.locateBrew(), appName: "AnyClip",
            releasesURL: UpdateChecker.releasesPageURL)
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/sh")
        proc.arguments = ["-c", script]
        do { try proc.run() }
        catch { openReleasesPage() }  // could not spawn helper → manual path
    }

    func openReleasesPage() {
        if let url = URL(string: UpdateChecker.releasesPageURL) {
            NSWorkspace.shared.open(url)
        }
    }

    private static func locateBrew() -> String {
        for p in ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
        where FileManager.default.isExecutableFile(atPath: p) { return p }
        return "brew"
    }
}
```

- [ ] **Step 2: Add version line + Check-for-Updates item to `StatusItemController`**

In the stored-property block (after `private let onNotificationsEnabled: () -> Void`) add:

```swift
    private let appVersion: String
    private let onCheckUpdates: () async -> UpdateStatus
    private let onInstallUpdate: () -> Void
    private let onOpenReleases: () -> Void
    private let versionItem: NSMenuItem
    private let checkUpdatesItem = NSMenuItem(
        title: "Check for Updates", action: nil, keyEquivalent: "")
    private enum UpdateMode { case idle, available(String), failed }
    private var updateMode: UpdateMode = .idle
    private var updateResetTimer: Timer?
```

Change the `init` signature and body. Replace:

```swift
    init(
        logFileURL: URL,
        onNotificationsEnabled: @escaping () -> Void,
        onQuit: @escaping () -> Void
    ) {
        self.logFileURL = logFileURL
        self.onNotificationsEnabled = onNotificationsEnabled
        self.onQuit = onQuit
```

with:

```swift
    init(
        logFileURL: URL,
        appVersion: String,
        onNotificationsEnabled: @escaping () -> Void,
        onCheckUpdates: @escaping () async -> UpdateStatus,
        onInstallUpdate: @escaping () -> Void,
        onOpenReleases: @escaping () -> Void,
        onQuit: @escaping () -> Void
    ) {
        self.logFileURL = logFileURL
        self.appVersion = appVersion
        self.onNotificationsEnabled = onNotificationsEnabled
        self.onCheckUpdates = onCheckUpdates
        self.onInstallUpdate = onInstallUpdate
        self.onOpenReleases = onOpenReleases
        self.onQuit = onQuit
        versionItem = NSMenuItem(
            title: "AnyClip v\(appVersion)", action: nil, keyEquivalent: "")
```

After `super.init()` and the existing `menu.autoenablesItems = false`, add:

```swift
        versionItem.isEnabled = false
        checkUpdatesItem.action = #selector(checkUpdates)
        checkUpdatesItem.target = self
```

Replace the `menu.addItem(...)` block with this order (adds `versionItem` + separator at top and `checkUpdatesItem` before the last separator):

```swift
        menu.addItem(versionItem)
        menu.addItem(.separator())
        menu.addItem(statusMenuItem)
        menu.addItem(lastSyncItem)
        menu.addItem(.separator())
        menu.addItem(tokenItem)
        menu.addItem(startAtLoginItem)
        menu.addItem(notificationsItem)
        menu.addItem(openLogsItem)
        menu.addItem(checkUpdatesItem)
        menu.addItem(.separator())
        menu.addItem(quitItem)
        statusItem.menu = menu
```

- [ ] **Step 3: Add the update actions to `StatusItemController`**

Add these methods (e.g., just before `@objc private func quit()`):

```swift
    // ---- updates --------------------------------------------------------

    @objc private func checkUpdates() {
        switch updateMode {
        case .available(let v):
            let confirm = NSAlert()
            confirm.messageText = "Update to v\(v)?"
            confirm.informativeText = "AnyClip will close, update via Homebrew, and reopen."
            confirm.addButton(withTitle: "Update")
            confirm.addButton(withTitle: "Cancel")
            guard confirm.runModal() == .alertFirstButtonReturn else { return }
            onInstallUpdate()           // caller spawns helper + quits
            return
        case .failed:
            onOpenReleases()
            setUpdateMode(.idle)
            checkUpdatesItem.title = "Check for Updates"
            return
        case .idle:
            break
        }
        checkUpdatesItem.title = "Checking…"
        checkUpdatesItem.isEnabled = false
        Task { @MainActor in
            let status = await onCheckUpdates()
            applyUpdateStatus(status, silent: false)
        }
    }

    /// Best-effort check on launch: only surface an available update; never
    /// show "up to date"/"failed" and never pop a dialog.
    func runSilentUpdateCheck() {
        Task { @MainActor in
            let status = await onCheckUpdates()
            if case .available = status { applyUpdateStatus(status, silent: true) }
        }
    }

    private func applyUpdateStatus(_ status: UpdateStatus, silent: Bool) {
        checkUpdatesItem.isEnabled = true
        switch status {
        case .upToDate(let cur):
            setUpdateMode(.idle)
            checkUpdatesItem.title = "You're up to date (v\(cur))"
            scheduleUpdateLabelReset()
        case .available(let latest, _):
            setUpdateMode(.available(latest))
            checkUpdatesItem.title = "Update to v\(latest)"
        case .failed:
            if silent { return }
            setUpdateMode(.failed)
            checkUpdatesItem.title = "Check failed — open releases"
        }
    }

    private func setUpdateMode(_ mode: UpdateMode) { updateMode = mode }

    private func scheduleUpdateLabelReset() {
        updateResetTimer?.invalidate()
        updateResetTimer = Timer.scheduledTimer(
            withTimeInterval: 3, repeats: false
        ) { [weak self] _ in
            MainActor.assumeIsolated {
                guard let self, case .idle = self.updateMode else { return }
                self.checkUpdatesItem.title = "Check for Updates"
            }
        }
    }
```

- [ ] **Step 4: Wire it in `AppDelegate.swift`**

Add a stored property to `AppDelegate`:

```swift
    private var updateService: UpdateService?
```

In `applicationDidFinishLaunching`, after `self.daemon = daemon`, construct the service and replace the `controller = StatusItemController(...)` call:

```swift
        let updateService = UpdateService(appVersion: appVersion)
        self.updateService = updateService

        controller = StatusItemController(
            logFileURL: logURL,
            appVersion: appVersion,
            onNotificationsEnabled: { [notifier] in notifier.setup() },
            onCheckUpdates: { await updateService.check() },
            onInstallUpdate: { [weak self] in
                self?.updateService?.installAndRelaunch()
                self?.quitGracefully()
            },
            onOpenReleases: { updateService.openReleasesPage() },
            onQuit: { [weak self] in self?.quitGracefully() })
        controller?.runSilentUpdateCheck()
```

- [ ] **Step 5: Build + full Swift suite**

Run: `swift build --package-path formacOS && swift test --package-path formacOS`
Expected: build succeeds; all tests pass (existing 114 + Task 1–2 additions).

- [ ] **Step 6: Manual smoke (build the app, click the menu)**

Run: `ANYCLIP_BUILD_VERSION=1.1.7 formacOS/Scripts/build-app.sh`
Then launch `formacOS/dist/AnyClip.app`, open the menu, confirm: top line `AnyClip v1.1.7`; click `Check for Updates` → shows `Checking…` then `You're up to date (v1.1.7)` (or `Update to vX` if a newer release exists). Do NOT click Update unless you intend to upgrade.

- [ ] **Step 7: Commit**

```bash
git add formacOS/Sources/AnyClipApp/UpdateService.swift formacOS/Sources/AnyClipApp/StatusItemController.swift formacOS/Sources/AnyClipApp/AppDelegate.swift
git commit -m "feat(macos): version line + Check for Updates (brew upgrade + relaunch)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 4: C# core — `UpdateChecker`

**Files:**
- Create: `forwindows/src/AnyClipCore/UpdateChecker.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/UpdateCheckerTests.cs`

**Interfaces:**
- Produces: `abstract record UpdateStatus` with nested `UpToDate(string Current)`, `Available(string Latest, string Url)`, `Failed(string Reason)`; `static class UpdateChecker` with `const string ReleasesApiUrl/ReleasesPageUrl`, `string? ParseLatestTag(string)`, `int CompareVersions(string,string)`, `Task<UpdateStatus> CheckForUpdateAsync(string current, Func<Task<string>> fetch)`.

- [ ] **Step 1: Write the failing test**

```csharp
// forwindows/tests/AnyClipCore.Tests/UpdateCheckerTests.cs
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public void CompareVersionsOrders()
    {
        Assert.True(UpdateChecker.CompareVersions("1.1.6", "1.1.7") < 0);
        Assert.Equal(0, UpdateChecker.CompareVersions("1.1.7", "1.1.7"));
        Assert.True(UpdateChecker.CompareVersions("1.2.0", "1.1.9") > 0);
        Assert.True(UpdateChecker.CompareVersions("1.1.10", "1.1.9") > 0);   // numeric
        Assert.True(UpdateChecker.CompareVersions("1.1.8-beta", "1.1.8") < 0); // pre-release lower
        Assert.True(UpdateChecker.CompareVersions("0.0.0-dev", "1.1.7") < 0);  // dev lowest
    }

    [Fact]
    public void ParseLatestTagStripsV()
    {
        Assert.Equal("1.1.7", UpdateChecker.ParseLatestTag("{\"tag_name\":\"v1.1.7\"}"));
        Assert.Equal("1.2.0", UpdateChecker.ParseLatestTag("{\"tag_name\":\"1.2.0\"}"));
        Assert.Null(UpdateChecker.ParseLatestTag("{\"no_tag\":true}"));
        Assert.Null(UpdateChecker.ParseLatestTag("not json"));
    }

    [Fact]
    public async Task CheckForUpdateClassifies()
    {
        var newer = await UpdateChecker.CheckForUpdateAsync("1.1.6",
            () => Task.FromResult("{\"tag_name\":\"v1.1.7\"}"));
        var a = Assert.IsType<UpdateStatus.Available>(newer);
        Assert.Equal("1.1.7", a.Latest);
        Assert.Equal(UpdateChecker.ReleasesPageUrl, a.Url);

        var same = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromResult("{\"tag_name\":\"v1.1.7\"}"));
        Assert.IsType<UpdateStatus.UpToDate>(same);

        var bad = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromResult("garbage"));
        Assert.IsType<UpdateStatus.Failed>(bad);

        var threw = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromException<string>(new Exception("boom")));
        Assert.IsType<UpdateStatus.Failed>(threw);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test forwindows/tests/AnyClipCore.Tests --filter UpdateCheckerTests`
Expected: FAIL (compile error — `UpdateChecker` does not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// forwindows/src/AnyClipCore/UpdateChecker.cs
using System.Text.Json;

namespace AnyClip.Core;

public abstract record UpdateStatus
{
    public sealed record UpToDate(string Current) : UpdateStatus;
    public sealed record Available(string Latest, string Url) : UpdateStatus;
    public sealed record Failed(string Reason) : UpdateStatus;
}

/// Pure update detection. Network IO is injected via `fetch` so the
/// parse/compare logic is unit-testable without hitting GitHub.
public static class UpdateChecker
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/gum798/AnyClip/releases/latest";
    public const string ReleasesPageUrl =
        "https://github.com/gum798/AnyClip/releases/latest";

    public static string? ParseLatestTag(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() is { } tag)
            {
                var v = tag.StartsWith('v') ? tag[1..] : tag;
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// Semver-ish: numeric components dominate; a pre-release ("-" suffix)
    /// ranks below the same core; non-numeric components sort low. Returns
    /// negative / 0 / positive like a comparator.
    public static int CompareVersions(string a, string b)
    {
        var (na, preA) = Parse(a);
        var (nb, preB) = Parse(b);
        int n = Math.Max(na.Count, nb.Count);
        for (int i = 0; i < n; i++)
        {
            int x = i < na.Count ? na[i] : 0;
            int y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        if (preA != preB) return preA ? -1 : 1;
        return 0;
    }

    private static (List<int> nums, bool isPre) Parse(string v)
    {
        var core = v.Split('-', 2)[0];
        var nums = core.Split('.')
            .Select(s => int.TryParse(s, out var n) ? n : -1).ToList();
        return (nums, v.Contains('-'));
    }

    public static async Task<UpdateStatus> CheckForUpdateAsync(
        string current, Func<Task<string>> fetch)
    {
        string body;
        try { body = await fetch(); }
        catch (Exception e) { return new UpdateStatus.Failed(e.Message); }
        var latest = ParseLatestTag(body);
        if (latest is null) return new UpdateStatus.Failed("could not parse latest release");
        return CompareVersions(current, latest) < 0
            ? new UpdateStatus.Available(latest, ReleasesPageUrl)
            : new UpdateStatus.UpToDate(current);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test forwindows/tests/AnyClipCore.Tests --filter UpdateCheckerTests`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add forwindows/src/AnyClipCore/UpdateChecker.cs forwindows/tests/AnyClipCore.Tests/UpdateCheckerTests.cs
git commit -m "feat(win): pure UpdateChecker (parse/compare/check)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 5: C# core — `UpdateCommand`

**Files:**
- Create: `forwindows/src/AnyClipCore/UpdateCommand.cs`
- Test: `forwindows/tests/AnyClipCore.Tests/UpdateCommandTests.cs`

**Interfaces:**
- Produces: `static class UpdateCommand { string WindowsHelperScript(int pid, string scoopInvocation, string exePath, string releasesUrl) }`.

- [ ] **Step 1: Write the failing test**

```csharp
// forwindows/tests/AnyClipCore.Tests/UpdateCommandTests.cs
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class UpdateCommandTests
{
    [Fact]
    public void WindowsHelperScriptHasAllPieces()
    {
        var s = UpdateCommand.WindowsHelperScript(
            4242, "scoop", @"C:\apps\AnyClip.exe", "https://example.test/r");
        Assert.Contains("Wait-Process -Id 4242", s);            // waits for our exit
        Assert.Contains("scoop update anyclip", s);
        Assert.Contains(@"Start-Process 'C:\apps\AnyClip.exe'", s); // relaunch on success
        Assert.Contains("Start-Process 'https://example.test/r'", s); // fallback on failure
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test forwindows/tests/AnyClipCore.Tests --filter UpdateCommandTests`
Expected: FAIL (compile error — `UpdateCommand` does not exist).

- [ ] **Step 3: Write minimal implementation**

```csharp
// forwindows/src/AnyClipCore/UpdateCommand.cs
namespace AnyClip.Core;

/// Pure builder for the detached PowerShell update helper (kept out of the
/// runtime so the exact command is unit-testable without spawning anything).
public static class UpdateCommand
{
    /// PowerShell -Command body: wait for `pid` to exit, run the scoop
    /// update, relaunch the exe; on failure open the releases page. Uses only
    /// single-quoted literals so it embeds safely in a double-quoted -Command.
    public static string WindowsHelperScript(
        int pid, string scoopInvocation, string exePath, string releasesUrl)
        => $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
         + $"try {{ {scoopInvocation} update anyclip; Start-Process '{exePath}' }} "
         + $"catch {{ Start-Process '{releasesUrl}' }}";
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test forwindows/tests/AnyClipCore.Tests --filter UpdateCommandTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add forwindows/src/AnyClipCore/UpdateCommand.cs forwindows/tests/AnyClipCore.Tests/UpdateCommandTests.cs
git commit -m "feat(win): pure UpdateCommand helper-script builder

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 6: C# runtime + UI — `UpdateService`, version header, Check-for-Updates

**Files:**
- Create: `forwindows/src/AnyClipApp/UpdateService.cs`
- Modify: `forwindows/src/AnyClipApp/TrayIcon.cs`
- Modify: `forwindows/src/AnyClipApp/Program.cs`

**Interfaces:**
- Consumes: `UpdateChecker`, `UpdateCommand`, `UpdateStatus` (Task 4–5).
- Produces: `sealed class UpdateService(string appVersion)` with `Task<UpdateStatus> CheckAsync()`, `void InstallAndRelaunch()`, `void OpenReleasesPage()`. `TrayIcon` ctor gains `string appVersion, Func<Task<UpdateStatus>> checkAsync, Action installUpdate, Action openReleases` and `Task RunSilentUpdateCheckAsync()`.

- [ ] **Step 1: Create `UpdateService.cs`**

```csharp
// forwindows/src/AnyClipApp/UpdateService.cs
using System.Diagnostics;
using AnyClip.Core;

namespace AnyClip.App;

/// Real network + process side of updates. Keeps TrayIcon free of IO.
public sealed class UpdateService(string appVersion)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public Task<UpdateStatus> CheckAsync()
        => UpdateChecker.CheckForUpdateAsync(appVersion, FetchLatestJsonAsync);

    private static async Task<string> FetchLatestJsonAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateChecker.ReleasesApiUrl);
        req.Headers.UserAgent.ParseAdd("AnyClip-updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// Spawn the detached upgrade helper, then the caller quits. The helper
    /// outlives us, runs `scoop update anyclip`, and relaunches the new exe.
    public void InstallAndRelaunch()
    {
        string exe = Environment.ProcessPath ?? Application.ExecutablePath;
        string script = UpdateCommand.WindowsHelperScript(
            Environment.ProcessId, "scoop", exe, UpdateChecker.ReleasesPageUrl);
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            RotatingLog.Shared.Warning($"update helper spawn failed: {e.Message}");
            OpenReleasesPage();
        }
    }

    public void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl)
            { UseShellExecute = true });
        }
        catch (Exception e)
        { RotatingLog.Shared.Warning($"open releases failed: {e.Message}"); }
    }
}
```

- [ ] **Step 2: Add version header + Check-for-Updates item to `TrayIcon`**

Add fields (next to the other `ToolStripMenuItem` fields near the top):

```csharp
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _checkUpdatesItem = new("Check for Updates");
    private readonly System.Windows.Forms.Timer _updateResetTimer = new() { Interval = 3000 };
    private readonly Func<Task<UpdateStatus>> _checkAsync;
    private readonly Action _installUpdate;
    private readonly Action _openReleases;
    private enum UpdateMode { Idle, Available, Failed }
    private UpdateMode _updateMode = UpdateMode.Idle;
    private string? _availableVersion;
```

Change the constructor signature from:

```csharp
    public TrayIcon(string logFile, NotificationSettings settings, Action onQuit)
    {
        _logFile = logFile;
        _settings = settings;
        _onQuit = onQuit;
```

to:

```csharp
    public TrayIcon(string logFile, NotificationSettings settings, string appVersion,
        Func<Task<UpdateStatus>> checkAsync, Action installUpdate, Action openReleases,
        Action onQuit)
    {
        _logFile = logFile;
        _settings = settings;
        _checkAsync = checkAsync;
        _installUpdate = installUpdate;
        _openReleases = openReleases;
        _onQuit = onQuit;
        _versionItem = new ToolStripMenuItem($"AnyClip v{appVersion}") { Enabled = false };
```

After the `_pulseTimer.Tick += ...` block, add the reset-timer and click wiring:

```csharp
        _updateResetTimer.Tick += (_, _) =>
        {
            _updateResetTimer.Stop();
            if (_updateMode == UpdateMode.Idle) _checkUpdatesItem.Text = "Check for Updates";
        };
        _checkUpdatesItem.Click += async (_, _) => await OnCheckUpdatesClickAsync();
```

Replace the `menu.Items.Add(...)` block with this order (version header + separator at top; Check-for-Updates above the final separator):

```csharp
        menu.Items.Add(_versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tokenItem);
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(_checkUpdatesItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);
```

- [ ] **Step 3: Add the update methods to `TrayIcon`**

Add (e.g., after `AnimateSyncPulse()`):

```csharp
    private async Task OnCheckUpdatesClickAsync()
    {
        switch (_updateMode)
        {
            case UpdateMode.Available:
                var ok = MessageBox.Show(
                    "AnyClip will close, update via Scoop, and reopen.",
                    $"Update to v{_availableVersion}?",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (ok == DialogResult.OK) _installUpdate();   // caller spawns helper + quits
                return;
            case UpdateMode.Failed:
                _openReleases();
                _updateMode = UpdateMode.Idle;
                _checkUpdatesItem.Text = "Check for Updates";
                return;
        }
        _checkUpdatesItem.Text = "Checking…";
        _checkUpdatesItem.Enabled = false;
        var status = await _checkAsync();   // resumes on UI thread (WinForms sync context)
        ApplyUpdateStatus(status, silent: false);
    }

    /// Best-effort check on launch: only surface an available update; never
    /// show "up to date"/"failed" and never pop a dialog.
    public async Task RunSilentUpdateCheckAsync()
    {
        var status = await _checkAsync();
        if (status is UpdateStatus.Available) ApplyUpdateStatus(status, silent: true);
    }

    private void ApplyUpdateStatus(UpdateStatus status, bool silent)
    {
        _checkUpdatesItem.Enabled = true;
        switch (status)
        {
            case UpdateStatus.UpToDate u:
                _updateMode = UpdateMode.Idle;
                _availableVersion = null;
                _checkUpdatesItem.Text = $"You're up to date (v{u.Current})";
                _updateResetTimer.Stop();
                _updateResetTimer.Start();
                break;
            case UpdateStatus.Available a:
                _updateMode = UpdateMode.Available;
                _availableVersion = a.Latest;
                _checkUpdatesItem.Text = $"Update to v{a.Latest}";
                break;
            case UpdateStatus.Failed:
                if (silent) return;
                _updateMode = UpdateMode.Failed;
                _availableVersion = null;
                _checkUpdatesItem.Text = "Check failed — open releases";
                break;
        }
    }
```

In `Dispose()`, add before `Notify.Visible = false;`:

```csharp
        _updateResetTimer.Stop();
        _updateResetTimer.Dispose();
```

- [ ] **Step 4: Wire it in `Program.cs`**

After `string appVersion = ...;` block, before `tray = new TrayIcon(...)`, add:

```csharp
        var updateService = new UpdateService(appVersion);
```

Replace `tray = new TrayIcon(logFile, notificationSettings, Quit);` with:

```csharp
        void InstallUpdate() { updateService.InstallAndRelaunch(); Quit(); }
        tray = new TrayIcon(logFile, notificationSettings, appVersion,
            updateService.CheckAsync, InstallUpdate, updateService.OpenReleasesPage, Quit);
```

After the daemon/events setup, just before `Application.Run();`, add the silent launch check (queued so it runs once the message loop is up):

```csharp
        staInvoker.BeginInvoke(new Action(async () =>
        {
            try { await tray.RunSilentUpdateCheckAsync(); }
            catch (Exception e) { RotatingLog.Shared.Warning($"silent update check failed: {e.Message}"); }
        }));
```

- [ ] **Step 5: Build + Core test suite (runs on macOS)**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet build forwindows/src/AnyClipApp && dotnet test forwindows/tests/AnyClipCore.Tests`
Expected: `AnyClipApp` compiles; all core tests pass (existing 59 + Task 4–5 additions).
Note: `AnyClipApp.Tests` is Windows-only; CI's `windows-native` job runs it. Do not block on it locally.

- [ ] **Step 6: Commit**

```bash
git add forwindows/src/AnyClipApp/UpdateService.cs forwindows/src/AnyClipApp/TrayIcon.cs forwindows/src/AnyClipApp/Program.cs
git commit -m "feat(win): version header + Check for Updates (scoop update + relaunch)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 7: Final verification

**Files:** none (verification only).

- [ ] **Step 1: Full Swift suite**

Run: `swift test --package-path formacOS`
Expected: all pass (incl. GoldenVector/Interop — unchanged).

- [ ] **Step 2: C# core suite**

Run: `export PATH="$HOME/.dotnet:$PATH"; dotnet test forwindows/tests/AnyClipCore.Tests`
Expected: all pass.

- [ ] **Step 3: Python suite (regression guard — should be untouched)**

Run: `.venv/bin/python -m pytest tests/ -q`
Expected: all pass (unchanged).

- [ ] **Step 4: Confirm no wire impact**

Run: `git diff --stat <plan-base>..HEAD -- formacOS/Sources/AnyClipCore/WireProtocol.swift forwindows/src/AnyClipCore/WireMessage.cs anyclip.py`
Expected: no changes to wire encoders (this feature is local to the apps).

- [ ] **Step 5: Update CLAUDE.md note (optional, recommended)**

The "native ports deliberately omit … Sparkle/WinSparkle auto-update" line is now partly outdated: the native apps do a package-manager-backed update check. Adjust that sentence to: "omit Sparkle/WinSparkle *background* auto-update (they offer a manual brew/scoop-backed Check for Updates instead)". Commit with the docs trailer.

---

## Self-Review

**Spec coverage:** version display (Task 3/6), GitHub-API check + injected fetch + compare (Task 1/4), install via brew/scoop + relaunch + browser fallback via detached helper (Task 2+3 / 5+6), menu states incl. silent launch check (Task 3/6), error handling → fallback (Service + UI states), tests for compare/parse/check/command (Task 1/2/4/5). All spec sections map to a task.

**Placeholders:** none — every code step has complete code.

**Type consistency:** `UpdateStatus` cases (`upToDate`/`available`/`failed`) consistent Swift↔C#; `checkForUpdate(current:fetch:)`/`CheckForUpdateAsync(current,fetch)`, `macHelperScript`/`WindowsHelperScript`, `installAndRelaunch`/`InstallAndRelaunch`, `runSilentUpdateCheck`/`RunSilentUpdateCheckAsync` match their call sites in Tasks 3/6.
