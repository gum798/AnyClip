# macOS Native Port (Swift, ./formacOS) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Native Swift macOS menu bar app in `formacOS/` that is wire-compatible with the Python AnyClip (protocol 1.0) and shares `~/.anyclip/`.

**Architecture:** SwiftPM package, three targets: `AnyClipCore` (pure logic, Swift 6 mode), `AnyClipDaemon` (Network.framework + NSPasteboard runtime, Swift 5 mode), `AnyClipApp` (AppKit shell). Tests use the bundled **swift-testing** framework (`import Testing`) because XCTest is not available with Command Line Tools-only toolchains. A build script assembles `AnyClip.app`.

**Tech Stack:** Swift 6.3 CLI toolchain (no Xcode), Network.framework, CryptoKit, AppKit, UserNotifications. Zero third-party packages.

**Spec:** `docs/superpowers/specs/2026-06-11-macos-native-port-design.md`

**Conventions for every task:**
- Working directory: repo root `/Users/seojeonghwa/project/AnyClip`. All swift commands take `--package-path formacOS`.
- Commit after every green test run. Commit messages end with `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`.
- The Python implementation at repo root (`anyclip.py`, `peer_state.py`, …) is the behavioral reference. When in doubt, match it.

---

### Task 1: Package scaffold + toolchain smoke test

**Files:**
- Create: `formacOS/Package.swift`
- Create: `formacOS/.gitignore`
- Create: `formacOS/Sources/AnyClipCore/Placeholder.swift` (replaced in Task 2)
- Create: `formacOS/Sources/AnyClipDaemon/Placeholder.swift` (replaced in Task 9)
- Create: `formacOS/Sources/AnyClipApp/main.swift` (minimal; replaced in Task 18)
- Test: `formacOS/Tests/AnyClipCoreTests/SmokeTests.swift`

- [ ] **Step 1: Create Package.swift**

```swift
// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "AnyClip",
    platforms: [.macOS(.v14)],
    targets: [
        .target(name: "AnyClipCore"),
        .target(
            name: "AnyClipDaemon",
            dependencies: ["AnyClipCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "AnyClipApp",
            dependencies: ["AnyClipCore", "AnyClipDaemon"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "AnyClipCoreTests",
            dependencies: ["AnyClipCore"],
            resources: [.copy("Fixtures")]
        ),
        .testTarget(
            name: "AnyClipDaemonTests",
            dependencies: ["AnyClipDaemon"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
```

- [ ] **Step 2: Create .gitignore**

`formacOS/.gitignore`:

```
.build/
dist/
*.xcodeproj
```

- [ ] **Step 3: Create placeholder sources**

`formacOS/Sources/AnyClipCore/Placeholder.swift`:

```swift
public enum AnyClipCoreMarker {
    public static let present = true
}
```

`formacOS/Sources/AnyClipDaemon/Placeholder.swift`:

```swift
import AnyClipCore

enum AnyClipDaemonMarker {
    static let present = AnyClipCoreMarker.present
}
```

`formacOS/Sources/AnyClipApp/main.swift`:

```swift
import AnyClipCore

print("AnyClip placeholder \(AnyClipCoreMarker.present)")
```

Also create the fixtures dir so `resources: [.copy("Fixtures")]` resolves:

```bash
mkdir -p formacOS/Tests/AnyClipCoreTests/Fixtures
touch formacOS/Tests/AnyClipCoreTests/Fixtures/.keepme
```

- [ ] **Step 4: Write smoke test**

`formacOS/Tests/AnyClipCoreTests/SmokeTests.swift`:

```swift
import Testing
@testable import AnyClipCore

@Test func toolchainSmoke() {
    #expect(AnyClipCoreMarker.present)
}
```

Also create `formacOS/Tests/AnyClipDaemonTests/SmokeTests.swift`:

```swift
import Testing
@testable import AnyClipDaemon

@Test func daemonTargetCompiles() {
    #expect(AnyClipDaemonMarker.present)
}
```

- [ ] **Step 5: Run tests**

Run: `swift test --package-path formacOS`
Expected: builds, 2 tests pass. **If swift-testing is unavailable** (build error on `import Testing`), STOP and report — the testing strategy must be revisited before continuing.

- [ ] **Step 6: Commit**

```bash
git add formacOS docs/superpowers/plans/2026-06-11-macos-native-port.md
git commit -m "formacOS: scaffold SwiftPM package (3 targets + swift-testing smoke)"
```

---

### Task 2: Core — SHA-256 helpers + VersionNegotiator

**Files:**
- Create: `formacOS/Sources/AnyClipCore/Hashing.swift`
- Create: `formacOS/Sources/AnyClipCore/VersionNegotiator.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/HashingTests.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/VersionNegotiatorTests.swift`

(`Placeholder.swift` stays for now — the daemon placeholder and `main.swift`
still reference the marker; it is removed in Tasks 9/18.)

Reference: `version_negotiator.py`, `tests/test_version_negotiator.py`, `anyclip.py:344-349`.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/HashingTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

@Test func sha256HexOfString() {
    // python: hashlib.sha256("hello".encode()).hexdigest()
    #expect(sha256Hex("hello") ==
        "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
}

@Test func sha256HexOfUnicodeString() {
    // python3 -c 'import hashlib; print(hashlib.sha256("안녕".encode()).hexdigest())'
    #expect(sha256Hex("안녕") ==
        "e8f817f346d1d411cc59d5bdda64fab3763890e1f0f8f4c15805cf78874d68bf")
}

@Test func sha256HexOfBytes() {
    #expect(sha256Hex(Data([0x00, 0x01, 0xff])) ==
        sha256Hex(Data([0x00, 0x01, 0xff])))
    #expect(sha256Hex(Data()) ==
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
}
```

`formacOS/Tests/AnyClipCoreTests/VersionNegotiatorTests.swift` (table from `tests/test_version_negotiator.py`):

```swift
import Testing
@testable import AnyClipCore

private func v(_ major: Int, _ minor: Int, app: String = "1.0.0") -> VersionInfo {
    VersionInfo(appVersion: app, protocolMajor: major, protocolMinor: minor)
}

@Test func sameVersionIsCompatible() {
    #expect(negotiate(local: v(1, 0), peer: v(1, 0)) == .compatible)
}

@Test func peerOlderMinorLinksWithHint() {
    let r = negotiate(local: v(1, 2), peer: v(1, 0))
    #expect(r == .peerOlderMinor)
    #expect(linkAllowed(r))
}

@Test func peerNewerMinorLinksWithHint() {
    let r = negotiate(local: v(1, 0), peer: v(1, 2))
    #expect(r == .peerNewerMinor)
    #expect(linkAllowed(r))
}

@Test func peerOlderMajorRefused() {
    let r = negotiate(local: v(2, 0), peer: v(1, 5))
    #expect(r == .peerOlderMajor)
    #expect(!linkAllowed(r))
}

@Test func peerNewerMajorRefused() {
    let r = negotiate(local: v(1, 9), peer: v(2, 0))
    #expect(r == .peerNewerMajor)
    #expect(!linkAllowed(r))
}

@Test func appVersionNeverAffectsOutcome() {
    #expect(negotiate(local: v(1, 0, app: "1.0.0"), peer: v(1, 0, app: "9.9.9")) == .compatible)
}

@Test func rawValuesMatchPythonEnum() {
    #expect(Compatibility.compatible.rawValue == "compatible")
    #expect(Compatibility.peerOlderMinor.rawValue == "peer_older_minor")
    #expect(Compatibility.peerNewerMinor.rawValue == "peer_newer_minor")
    #expect(Compatibility.peerOlderMajor.rawValue == "peer_older_major")
    #expect(Compatibility.peerNewerMajor.rawValue == "peer_newer_major")
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `swift test --package-path formacOS`
Expected: compile FAILURE (`sha256Hex`, `VersionInfo` undefined).

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/Hashing.swift`:

```swift
import CryptoKit
import Foundation

/// Hex SHA-256 of raw bytes — same as Python's hashlib.sha256(data).hexdigest().
public func sha256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

/// Hex SHA-256 of the UTF-8 encoding of `text`.
public func sha256Hex(_ text: String) -> String {
    sha256Hex(Data(text.utf8))
}
```

`formacOS/Sources/AnyClipCore/VersionNegotiator.swift`:

```swift
/// Pure version negotiation between two AnyClip peers.
/// Port of version_negotiator.py — keep the table in lockstep.

public enum Compatibility: String, Sendable, Equatable {
    case compatible = "compatible"
    case peerOlderMinor = "peer_older_minor"
    case peerNewerMinor = "peer_newer_minor"
    case peerOlderMajor = "peer_older_major"
    case peerNewerMajor = "peer_newer_major"
}

public struct VersionInfo: Sendable, Equatable {
    public let appVersion: String
    public let protocolMajor: Int
    public let protocolMinor: Int

    public init(appVersion: String, protocolMajor: Int, protocolMinor: Int) {
        self.appVersion = appVersion
        self.protocolMajor = protocolMajor
        self.protocolMinor = protocolMinor
    }
}

/// Major version dominates: any major mismatch is a refusal regardless of
/// minor. Minor differences are advisory and keep the link. appVersion is
/// informational and never affects the outcome.
public func negotiate(local: VersionInfo, peer: VersionInfo) -> Compatibility {
    if peer.protocolMajor < local.protocolMajor { return .peerOlderMajor }
    if peer.protocolMajor > local.protocolMajor { return .peerNewerMajor }
    if peer.protocolMinor < local.protocolMinor { return .peerOlderMinor }
    if peer.protocolMinor > local.protocolMinor { return .peerNewerMinor }
    return .compatible
}

public func linkAllowed(_ result: Compatibility) -> Bool {
    switch result {
    case .compatible, .peerOlderMinor, .peerNewerMinor: return true
    case .peerOlderMajor, .peerNewerMajor: return false
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `swift test --package-path formacOS`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: port sha256 helpers + version negotiator"
```

---

### Task 3: Core — DaemonEvent + PeerStateReducer

**Files:**
- Create: `formacOS/Sources/AnyClipCore/PeerState.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/PeerStateTests.swift`

Reference: `peer_state.py`, `tests/test_peer_state.py`.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/PeerStateTests.swift`:

```swift
import Testing
@testable import AnyClipCore

@Test func initialIsIdle() {
    #expect(PeerUIState.initial.kind == .idle)
}

@Test func linkUpProducesLinkedWithTimestamp() {
    let s = reducePeerState(.initial, .linkUp(peerName: "win-pc", peerID: "abc"), now: 42.0)
    #expect(s.kind == .linked)
    #expect(s.peerName == "win-pc")
    #expect(s.since == 42.0)
    #expect(s.consecutiveHandshakeFails == 0)
}

@Test func linkDownGoesBackToSearching() {
    let linked = reducePeerState(.initial, .linkUp(peerName: "p", peerID: "x"), now: 1)
    let s = reducePeerState(linked, .linkDown(reason: "peer disconnected"), now: 2)
    #expect(s.kind == .searching)
    #expect(s.reason == "peer disconnected")
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
    let linked = reducePeerState(.initial, .linkUp(peerName: "p", peerID: "x"), now: 1)
    let s = reducePeerState(linked, .peerDiscovered(name: "n", addr: "a"), now: 2)
    #expect(s == linked)
}

@Test func permissionMissingIsError() {
    let s = reducePeerState(.initial, .permissionMissing(kind: "local_network"), now: 1)
    #expect(s.kind == .error)
    #expect(s.reason == "local_network")
}

@Test func fiveHandshakeFailsTripAuthError() {
    var s = PeerUIState.initial
    for i in 1...4 {
        s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: Double(i))
        #expect(s.kind == .idle)
        #expect(s.consecutiveHandshakeFails == i)
    }
    s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: 5)
    #expect(s.kind == .error)
    #expect(s.reason == "auth")
}

@Test func linkUpResetsFailCounter() {
    var s = PeerUIState.initial
    s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: 1)
    s = reducePeerState(s, .linkUp(peerName: "p", peerID: "x"), now: 2)
    #expect(s.consecutiveHandshakeFails == 0)
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`
Expected: FAIL (`PeerUIState` undefined).

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/PeerState.swift`:

```swift
/// Daemon-event types and pure state-machine reducer for the UI shell.
/// Port of peer_state.py.

public enum DaemonEvent: Sendable, Equatable {
    case peerDiscovered(name: String, addr: String)
    case linkUp(peerName: String, peerID: String)
    case linkDown(reason: String)
    case handshakeFailed(addr: String, reason: String)
    case permissionMissing(kind: String)
}

public struct PeerUIState: Sendable, Equatable {
    public enum Kind: String, Sendable { case idle, searching, linked, error }

    public var kind: Kind
    public var peerName: String?
    public var since: Double?
    public var reason: String?
    /// Internal bookkeeping so the reducer can trip into error("auth")
    /// after a run of failed handshakes. UI reads kind/peerName/since/reason.
    public var consecutiveHandshakeFails: Int

    public init(
        kind: Kind,
        peerName: String? = nil,
        since: Double? = nil,
        reason: String? = nil,
        consecutiveHandshakeFails: Int = 0
    ) {
        self.kind = kind
        self.peerName = peerName
        self.since = since
        self.reason = reason
        self.consecutiveHandshakeFails = consecutiveHandshakeFails
    }

    public static let initial = PeerUIState(kind: .idle)
}

public let handshakeFailThreshold = 5

public func reducePeerState(
    _ prev: PeerUIState, _ event: DaemonEvent, now: Double
) -> PeerUIState {
    switch event {
    case .permissionMissing(let kind):
        return PeerUIState(kind: .error, reason: kind)
    case .linkUp(let peerName, _):
        return PeerUIState(kind: .linked, peerName: peerName, since: now)
    case .linkDown(let reason):
        return PeerUIState(kind: .searching, reason: reason)
    case .peerDiscovered:
        if prev.kind == .idle || prev.kind == .error {
            return PeerUIState(kind: .searching)
        }
        return prev
    case .handshakeFailed:
        var next = prev
        next.consecutiveHandshakeFails += 1
        if next.consecutiveHandshakeFails >= handshakeFailThreshold {
            return PeerUIState(
                kind: .error, reason: "auth",
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        return next
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: port peer-state reducer + daemon events"
```

---

### Task 4: Core — EchoSuppressor + AuthGate

**Files:**
- Create: `formacOS/Sources/AnyClipCore/EchoSuppressor.swift`
- Create: `formacOS/Sources/AnyClipCore/AuthGate.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/EchoSuppressorTests.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/AuthGateTests.swift`

Reference: `anyclip.py:789-806` (EchoSuppressor), `anyclip.py:1044-1083` (AuthGate).

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/EchoSuppressorTests.swift`:

```swift
import Testing
@testable import AnyClipCore

@Test func sendsWhenNothingReceived() {
    let s = EchoSuppressor()
    #expect(s.shouldSend(kind: "text", payloadHash: "h1"))
}

@Test func suppressesEchoOfJustReceived() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1")
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1"))
    #expect(s.shouldSend(kind: "text", payloadHash: "h2"))
}

@Test func kindsAreTrackedIndependently() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1")
    #expect(s.shouldSend(kind: "image", payloadHash: "h1"))
}
```

`formacOS/Tests/AnyClipCoreTests/AuthGateTests.swift`:

```swift
import Testing
@testable import AnyClipCore

/// Deterministic clock for AuthGate tests.
private final class FakeClock: @unchecked Sendable {
    var t: Double = 1000
    func now() -> Double { t }
}

@Test func notBlockedBeforeThreshold() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<4 { gate.recordFail("10.0.0.1") }
    #expect(!gate.isBlocked("10.0.0.1"))
}

@Test func blockedAtThresholdWithinCooldown() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<5 { gate.recordFail("10.0.0.1") }
    #expect(gate.isBlocked("10.0.0.1"))
    #expect(!gate.isBlocked("10.0.0.2"))
}

@Test func cooldownExpires() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<5 { gate.recordFail("10.0.0.1") }
    clock.t += 61
    #expect(!gate.isBlocked("10.0.0.1"))
}

@Test func successClearsCounter() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<5 { gate.recordFail("10.0.0.1") }
    gate.recordOK("10.0.0.1")
    #expect(!gate.isBlocked("10.0.0.1"))
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/EchoSuppressor.swift`:

```swift
/// Tracks the hash of the last item received from a peer per kind, so the
/// clipboard poller does not bounce a peer's update right back at them.
/// Text/image/file are tracked separately. Port of anyclip.EchoSuppressor.
public struct EchoSuppressor: Sendable {
    private var last: [String: String] = [:]

    public init() {}

    public mutating func markReceived(kind: String, payloadHash: String) {
        last[kind] = payloadHash
    }

    public func shouldSend(kind: String, payloadHash: String) -> Bool {
        last[kind] != payloadHash
    }
}
```

`formacOS/Sources/AnyClipCore/AuthGate.swift`:

```swift
import Foundation

/// Per-IP cooldown after repeated handshake failures. After maxFails failed
/// handshakes from the same IP, that IP is blocked for cooldown seconds.
/// A successful handshake clears the counter. Stale entries are swept lazily.
/// Port of anyclip.AuthGate; the caller (PeerLink actor) provides isolation.
public struct AuthGate: Sendable {
    public static let maxFails = 5
    public static let cooldown: Double = 60.0

    private var fails: [String: (count: Int, last: Double)] = [:]
    private let now: @Sendable () -> Double

    public init(now: @escaping @Sendable () -> Double = { Date().timeIntervalSince1970 }) {
        self.now = now
    }

    public mutating func isBlocked(_ ip: String) -> Bool {
        sweep()
        guard let entry = fails[ip] else { return false }
        return entry.count >= Self.maxFails && (now() - entry.last) < Self.cooldown
    }

    public mutating func recordFail(_ ip: String) {
        let count = fails[ip]?.count ?? 0
        fails[ip] = (count + 1, now())
    }

    public mutating func recordOK(_ ip: String) {
        fails[ip] = nil
    }

    private mutating func sweep() {
        let t = now()
        fails = fails.filter { t - $0.value.last < Self.cooldown }
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: port echo suppressor + auth gate"
```

---

### Task 5: Core — ConfigStore

**Files:**
- Create: `formacOS/Sources/AnyClipCore/ConfigStore.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/ConfigStoreTests.swift`

Reference: `config_store.py`, `tests/test_config_store.py`.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/ConfigStoreTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-test-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func loadMissingReturnsNil() {
    #expect(ConfigStore.load(dir: tempDir()) == nil)
}

@Test func saveThenLoadRoundTrips() throws {
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "secret-token"), dir: dir)
    #expect(ConfigStore.load(dir: dir)?.token == "secret-token")
}

@Test func savedFileHas0600Permissions() throws {
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "t"), dir: dir)
    let attrs = try FileManager.default.attributesOfItem(
        atPath: ConfigStore.configPath(dir: dir).path)
    #expect((attrs[.posixPermissions] as? Int) == 0o600)
}

@Test func savedFileIsReadableByPythonShape() throws {
    // Python json.load() must see {"token": "..."}.
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "abc"), dir: dir)
    let raw = try Data(contentsOf: ConfigStore.configPath(dir: dir))
    let obj = try JSONSerialization.jsonObject(with: raw) as? [String: Any]
    #expect(obj?["token"] as? String == "abc")
}

@Test func loadsPythonWrittenFile() throws {
    // Same shape config_store.py writes: indent=2, sort_keys, trailing newline.
    let dir = tempDir()
    let body = "{\n  \"token\": \"from-python\"\n}\n"
    try body.write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir)?.token == "from-python")
}

@Test func corruptFileReturnsNil() throws {
    let dir = tempDir()
    try "not json{{{".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func missingTokenKeyReturnsNil() throws {
    let dir = tempDir()
    try "{\"other\": 1}".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func emptyTokenReturnsNil() throws {
    let dir = tempDir()
    try "{\"token\": \"\"}".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func generatedTokensAreUrlSafeAndUnique() {
    let a = ConfigStore.generateToken()
    let b = ConfigStore.generateToken()
    #expect(a != b)
    #expect(a.count >= 42) // 32 bytes base64url ≈ 43 chars
    let allowed = CharacterSet(charactersIn:
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
    #expect(a.unicodeScalars.allSatisfy { allowed.contains($0) })
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/ConfigStore.swift`:

```swift
import Foundation

/// Persistent on-disk store for AnyClip's shared secret token.
/// Port of config_store.py. Shares ~/.anyclip/config.json with the Python
/// implementation — both sides read/write the same {"token": "..."} shape.

public struct StoredConfig: Sendable, Equatable {
    public var token: String
    public init(token: String) { self.token = token }
}

public enum ConfigStore {
    public static func defaultDir() -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true)
    }

    public static func configPath(dir: URL? = nil) -> URL {
        (dir ?? defaultDir()).appendingPathComponent("config.json")
    }

    /// 32 bytes of entropy, base64url without padding — same shape as
    /// Python's secrets.token_urlsafe(32).
    public static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        for i in bytes.indices { bytes[i] = UInt8.random(in: .min ... .max) }
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// Read the config file. nil if missing or unreadable/corrupt —
    /// a damaged file never blocks startup.
    public static func load(dir: URL? = nil) -> StoredConfig? {
        guard let raw = try? Data(contentsOf: configPath(dir: dir)) else { return nil }
        guard let obj = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let token = obj["token"] as? String, !token.isEmpty
        else { return nil }
        return StoredConfig(token: token)
    }

    /// Atomically write the config with 0600 permissions: temp file in the
    /// same directory, chmod, then rename(2).
    public static func save(_ config: StoredConfig, dir: URL? = nil) throws {
        let targetDir = dir ?? defaultDir()
        try FileManager.default.createDirectory(at: targetDir, withIntermediateDirectories: true)
        let target = configPath(dir: targetDir)
        let data = try JSONSerialization.data(
            withJSONObject: ["token": config.token],
            options: [.prettyPrinted, .sortedKeys])
        let tmp = targetDir.appendingPathComponent(".config.json.\(UUID().uuidString).tmp")
        do {
            try (data + Data("\n".utf8)).write(to: tmp)
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600], ofItemAtPath: tmp.path)
            guard rename(tmp.path, target.path) == 0 else {
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            throw error
        }
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: port config store (shared ~/.anyclip/config.json)"
```

---

### Task 6: Core — Wire protocol (messages, framing, clip payloads, helpers)

**Files:**
- Create: `formacOS/Sources/AnyClipCore/WireProtocol.swift`
- Create: `formacOS/Sources/AnyClipCore/TextHelpers.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/TextHelpersTests.swift`

Reference: `anyclip.py:1217-1229` (hello), `anyclip.py:1411-1440` (_send/_recv), `anyclip.py:1462-1511` (send_clip), `anyclip.py:525-530` (preview), `anyclip.py:1016-1022` (filename sanitize).

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/WireProtocolTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

@Test func frameIsFourByteBigEndianLengthPlusUTF8JSON() throws {
    let msg = WireMessage.ping(ts: 1.5)
    let frame = try msg.encodeFrame()
    let n = WireMessage.frameLength(frame.prefix(4))
    #expect(n == frame.count - 4)
    let body = try JSONSerialization.jsonObject(
        with: frame.dropFirst(4)) as? [String: Any]
    #expect(body?["type"] as? String == "ping")
    #expect(body?["ts"] as? Double == 1.5)
}

@Test func helloCarriesAllProtocolFields() throws {
    let msg = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "node-1", name: "mac", appVersion: "1.2.3")
    let frame = try msg.encodeFrame()
    let body = try JSONSerialization.jsonObject(
        with: frame.dropFirst(4)) as? [String: Any]
    #expect(body?["type"] as? String == "hello")
    #expect(body?["token"] as? String == sha256Hex("tok"))
    #expect(body?["node_id"] as? String == "node-1")
    #expect(body?["name"] as? String == "mac")
    #expect(body?["version"] as? Int == 1)          // legacy field MUST exist
    #expect(body?["app_version"] as? String == "1.2.3")
    #expect(body?["protocol_major"] as? Int == 1)
    #expect(body?["protocol_minor"] as? Int == 0)
}

@Test func clipTextRoundTrip() throws {
    let msg = WireMessage.clipText("안녕 AnyClip 👋", ts: 2.0)
    let frame = try msg.encodeFrame()
    let decoded = WireMessage.decodeBody(frame.dropFirst(4))
    #expect(decoded?.type == "clip")
    #expect(decoded?.kind == "text")
    #expect(decoded?.content == "안녕 AnyClip 👋")
    #expect(decoded?.hash == sha256Hex("안녕 AnyClip 👋"))
}

@Test func clipImageBase64AndByteCount() throws {
    let png = Data([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF])
    let msg = WireMessage.clipImage(png, ts: 3.0)
    #expect(msg.kind == "image")
    #expect(msg.bytes == png.count)
    #expect(msg.hash == sha256Hex(png))
    #expect(strictBase64Decode(msg.content!) == png)
}

@Test func clipFileCarriesName() throws {
    let data = Data("file body".utf8)
    let msg = WireMessage.clipFile(name: "réport.txt", data: data, ts: 4.0)
    #expect(msg.kind == "file")
    #expect(msg.name == "réport.txt")
    #expect(msg.bytes == data.count)
    #expect(strictBase64Decode(msg.content!) == data)
}

@Test func oversizedPayloadThrows() {
    var big = WireMessage.ping(ts: 0)
    big.content = String(repeating: "x", count: Wire.maxPayload + 1)
    #expect(throws: WireFrameError.self) { try big.encodeFrame() }
}

@Test func decodeBodyToleratesUnknownFields() {
    let raw = Data(#"{"type":"hello","token":"t","future_field":[1,2]}"#.utf8)
    let msg = WireMessage.decodeBody(raw)
    #expect(msg?.type == "hello")
    #expect(msg?.token == "t")
}

@Test func decodeBodyRejectsBadJSON() {
    #expect(WireMessage.decodeBody(Data("{notjson".utf8)) == nil)
}

@Test func peerVersionInfoFallsBackToLegacyVersion() {
    // Old peer sends only `version`; treat it as protocol_major, minor 0.
    var msg = WireMessage(type: "hello")
    msg.version = 1
    let v = msg.peerVersionInfo()
    #expect(v.protocolMajor == 1)
    #expect(v.protocolMinor == 0)
    #expect(v.appVersion == "unknown")
}

@Test func peerVersionInfoPrefersExplicitFields() {
    var msg = WireMessage(type: "hello")
    msg.version = 1
    msg.protocol_major = 2
    msg.protocol_minor = 3
    msg.app_version = "9.9.9"
    let v = msg.peerVersionInfo()
    #expect(v.protocolMajor == 2)
    #expect(v.protocolMinor == 3)
    #expect(v.appVersion == "9.9.9")
}

@Test func strictBase64RejectsGarbage() {
    #expect(strictBase64Decode("!!!not-base64!!!") == nil)
}

@Test func frameLengthParsesBigEndian() {
    #expect(WireMessage.frameLength(Data([0x00, 0x00, 0x01, 0x02])) == 258)
    #expect(WireMessage.frameLength(Data([0x01, 0x00, 0x00, 0x00])) == 16_777_216)
}
```

`formacOS/Tests/AnyClipCoreTests/TextHelpersTests.swift`:

```swift
import Testing
@testable import AnyClipCore

@Test func previewCollapsesNewlinesAndTruncates() {
    #expect(preview("a\nb\rc") == "a b c")
    #expect(preview("") == "(empty)")
    let long = String(repeating: "x", count: 100)
    #expect(preview(long) == String(repeating: "x", count: 80) + "...")
}

@Test func sanitizeKeepsSafeChars() {
    #expect(sanitizeFilename("report v2.txt") == "report v2.txt")
    #expect(sanitizeFilename("a/b/c.txt") == "c.txt")        // basename only
    #expect(sanitizeFilename("we!rd:na?me") == "we_rd_na_me")
    #expect(sanitizeFilename("") == "received.bin")
    #expect(sanitizeFilename("   ") == "received.bin")
    #expect(sanitizeFilename("한글파일.txt") == "한글파일.txt") // unicode alnum kept
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/WireProtocol.swift`:

```swift
import Foundation

/// Protocol constants — keep in lockstep with anyclip.py.
public enum Wire {
    public static let maxPayload = 16 * 1024 * 1024
    public static let protocolMajor = 1
    public static let protocolMinor = 0
    /// Legacy single-int field old peers read; equals protocolMajor.
    public static let legacyVersion = 1
    public static let defaultPort: UInt16 = 24816
    public static let serviceType = "_anyclip._tcp"
    public static let handshakeTimeout: Double = 5.0
    public static let connectTimeout: Double = 5.0
    /// Window after link-up in which a duplicate handshake is a connect
    /// race (node_id tie-breaker); later arrivals replace a stale link.
    public static let raceWindow: Double = 1.5
    public static let maxReconnectFails = 3
}

public enum WireFrameError: Error, Equatable {
    case payloadTooLarge(Int)
}

/// A semantic clipboard payload, decoupled from its wire encoding.
public enum ClipPayload: Sendable {
    case text(String)
    case image(Data)
    case file(name: String, data: Data)

    public var kind: String {
        switch self {
        case .text: return "text"
        case .image: return "image"
        case .file: return "file"
        }
    }

    public var payloadHash: String {
        switch self {
        case .text(let s): return sha256Hex(s)
        case .image(let d): return sha256Hex(d)
        case .file(_, let d): return sha256Hex(d)
        }
    }
}

/// One wire message. A single optional-field struct covers every frame type
/// (hello/clip/ping/pong) — JSONEncoder omits nil fields, JSONDecoder
/// tolerates extras, which matches the Python dict-based protocol.
/// snake_case property names ARE the wire field names; do not rename.
public struct WireMessage: Codable, Sendable, Equatable {
    public var type: String
    public var token: String?
    public var node_id: String?
    public var name: String?
    public var version: Int?
    public var app_version: String?
    public var protocol_major: Int?
    public var protocol_minor: Int?
    public var kind: String?
    public var content: String?
    public var hash: String?
    public var ts: Double?
    public var bytes: Int?

    public init(type: String) { self.type = type }
}

extension WireMessage {
    public static func hello(
        tokenHash: String, nodeID: String, name: String, appVersion: String
    ) -> WireMessage {
        var m = WireMessage(type: "hello")
        m.token = tokenHash
        m.node_id = nodeID
        m.name = name
        m.version = Wire.legacyVersion
        m.app_version = appVersion
        m.protocol_major = Wire.protocolMajor
        m.protocol_minor = Wire.protocolMinor
        return m
    }

    public static func clipText(_ text: String, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "text"
        m.content = text
        m.hash = sha256Hex(text)
        m.ts = ts
        return m
    }

    public static func clipImage(_ png: Data, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "image"
        m.content = png.base64EncodedString()
        m.hash = sha256Hex(png)
        m.ts = ts
        m.bytes = png.count
        return m
    }

    public static func clipFile(name: String, data: Data, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "file"
        m.name = name
        m.content = data.base64EncodedString()
        m.hash = sha256Hex(data)
        m.ts = ts
        m.bytes = data.count
        return m
    }

    public static func clip(_ payload: ClipPayload, ts: Double) -> WireMessage {
        switch payload {
        case .text(let s): return clipText(s, ts: ts)
        case .image(let d): return clipImage(d, ts: ts)
        case .file(let n, let d): return clipFile(name: n, data: d, ts: ts)
        }
    }

    public static func ping(ts: Double) -> WireMessage {
        var m = WireMessage(type: "ping")
        m.ts = ts
        return m
    }

    public static func pong(ts: Double) -> WireMessage {
        var m = WireMessage(type: "pong")
        m.ts = ts
        return m
    }
}

extension WireMessage {
    /// 4-byte big-endian length prefix + UTF-8 JSON body.
    public func encodeFrame() throws -> Data {
        let body = try JSONEncoder().encode(self)
        guard body.count <= Wire.maxPayload else {
            throw WireFrameError.payloadTooLarge(body.count)
        }
        var out = Data(capacity: 4 + body.count)
        let n = UInt32(body.count)
        out.append(UInt8((n >> 24) & 0xFF))
        out.append(UInt8((n >> 16) & 0xFF))
        out.append(UInt8((n >> 8) & 0xFF))
        out.append(UInt8(n & 0xFF))
        out.append(body)
        return out
    }

    /// Big-endian length from the 4-byte header. Alignment-safe for slices.
    public static func frameLength(_ header: Data) -> Int {
        var n = 0
        for byte in header.prefix(4) { n = (n << 8) | Int(byte) }
        return n
    }

    /// nil on malformed JSON — caller treats that as end-of-session.
    public static func decodeBody(_ body: Data) -> WireMessage? {
        try? JSONDecoder().decode(WireMessage.self, from: body)
    }

    /// Peer version with backward-compat defaults: an old peer only sends
    /// `version`, treated as protocol_major with minor 0 / unknown app.
    public func peerVersionInfo() -> VersionInfo {
        let major = protocol_major ?? version ?? 0
        let app = (app_version?.isEmpty == false) ? app_version! : "unknown"
        return VersionInfo(appVersion: app, protocolMajor: major,
                           protocolMinor: protocol_minor ?? 0)
    }
}

/// Strict base64 decode — Data(base64Encoded:) rejects invalid input,
/// mirroring Python's b64decode(validate=True).
public func strictBase64Decode(_ s: String) -> Data? {
    Data(base64Encoded: s)
}
```

`formacOS/Sources/AnyClipCore/TextHelpers.swift`:

```swift
import Foundation

/// One-line preview suitable for a toast body. Port of anyclip.preview().
public func preview(_ text: String, maxLen: Int = 80) -> String {
    let snippet = text
        .replacingOccurrences(of: "\r", with: " ")
        .replacingOccurrences(of: "\n", with: " ")
        .trimmingCharacters(in: .whitespaces)
    if snippet.isEmpty { return "(empty)" }
    if snippet.count <= maxLen { return snippet }
    return String(snippet.prefix(maxLen)) + "..."
}

/// Sanitize an inbound file name: basename only, then replace anything
/// outside [unicode-alnum . _ - space] with "_". Port of
/// ClipboardWatcher.update_local_file's sanitizer.
public func sanitizeFilename(_ name: String) -> String {
    let base = (name as NSString).lastPathComponent
        .trimmingCharacters(in: .whitespaces)
    guard !base.isEmpty else { return "received.bin" }
    let allowed = CharacterSet.alphanumerics
        .union(CharacterSet(charactersIn: "._- "))
    var out = ""
    for scalar in base.unicodeScalars {
        out.append(allowed.contains(scalar) ? Character(scalar) : "_")
    }
    return out
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: wire protocol codec + clip payloads + text helpers"
```

---

### Task 7: Golden vectors from the Python implementation

Frames generated with the same code-paths as `anyclip.py` are committed as
fixtures; Swift tests decode them. This pins cross-implementation
compatibility without needing Python at test time.

**Files:**
- Create: `formacOS/Scripts/gen-golden-vectors.py`
- Create (generated): `formacOS/Tests/AnyClipCoreTests/Fixtures/hello.bin`, `clip_text.bin`, `clip_image.bin`, `clip_file.bin`, `ping.bin`, `manifest.json`
- Test: `formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift`

- [ ] **Step 1: Write the generator script**

`formacOS/Scripts/gen-golden-vectors.py`:

```python
#!/usr/bin/env python3
"""Generate wire-protocol golden vectors using the exact encoding rules of
anyclip.py (_send): json.dumps(ensure_ascii=False).encode("utf-8") behind a
4-byte big-endian length prefix. Stdlib only. Re-run when the protocol
changes; fixtures are committed.
"""
import base64
import hashlib
import json
import pathlib

OUT = pathlib.Path(__file__).resolve().parent.parent / "Tests" / "AnyClipCoreTests" / "Fixtures"

TOKEN = "golden-token"
TOKEN_HASH = hashlib.sha256(TOKEN.encode("utf-8")).hexdigest()
NODE_ID = "11111111-2222-3333-4444-555555555555"
TEXT = "안녕 AnyClip 👋 line1\nline2"
IMAGE_BYTES = b"\x89PNG\r\n\x1a\n" + bytes(range(64))
FILE_NAME = "réport final.txt"
FILE_BYTES = b"golden file body \x00\x01\x02"
TS = 1718000000.5


def frame(obj: dict) -> bytes:
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    return len(data).to_bytes(4, "big") + data


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    vectors = {
        "hello.bin": {
            "type": "hello", "token": TOKEN_HASH, "node_id": NODE_ID,
            "name": "golden-mac", "version": 1, "app_version": "1.0.0",
            "protocol_major": 1, "protocol_minor": 0,
        },
        "clip_text.bin": {
            "type": "clip", "kind": "text", "content": TEXT,
            "hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(), "ts": TS,
        },
        "clip_image.bin": {
            "type": "clip", "kind": "image",
            "content": base64.b64encode(IMAGE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(IMAGE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(IMAGE_BYTES),
        },
        "clip_file.bin": {
            "type": "clip", "kind": "file", "name": FILE_NAME,
            "content": base64.b64encode(FILE_BYTES).decode("ascii"),
            "hash": hashlib.sha256(FILE_BYTES).hexdigest(), "ts": TS,
            "bytes": len(FILE_BYTES),
        },
        "ping.bin": {"type": "ping", "ts": TS},
    }
    for fname, obj in vectors.items():
        (OUT / fname).write_bytes(frame(obj))
    manifest = {
        "token": TOKEN, "token_hash": TOKEN_HASH, "node_id": NODE_ID,
        "text": TEXT,
        "text_hash": hashlib.sha256(TEXT.encode("utf-8")).hexdigest(),
        "image_b64": base64.b64encode(IMAGE_BYTES).decode("ascii"),
        "image_hash": hashlib.sha256(IMAGE_BYTES).hexdigest(),
        "file_name": FILE_NAME,
        "file_b64": base64.b64encode(FILE_BYTES).decode("ascii"),
        "file_hash": hashlib.sha256(FILE_BYTES).hexdigest(),
        "ts": TS,
    }
    (OUT / "manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"wrote {len(vectors) + 1} fixtures to {OUT}")


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Generate fixtures**

Run: `python3 formacOS/Scripts/gen-golden-vectors.py`
Expected: `wrote 6 fixtures to .../Fixtures`. Then `rm formacOS/Tests/AnyClipCoreTests/Fixtures/.keepme`.

- [ ] **Step 3: Write the golden tests**

`formacOS/Tests/AnyClipCoreTests/GoldenVectorTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

private func fixture(_ name: String) throws -> Data {
    let url = Bundle.module.url(forResource: name, withExtension: nil,
                                subdirectory: "Fixtures")!
    return try Data(contentsOf: url)
}

private func manifest() throws -> [String: Any] {
    try JSONSerialization.jsonObject(with: fixture("manifest.json")) as! [String: Any]
}

private func decodeGoldenFrame(_ name: String) throws -> WireMessage {
    let frame = try fixture(name)
    let n = WireMessage.frameLength(frame.prefix(4))
    #expect(n == frame.count - 4)
    let msg = WireMessage.decodeBody(frame.dropFirst(4))
    #expect(msg != nil)
    return msg!
}

@Test func goldenHelloDecodes() throws {
    let m = try decodeGoldenFrame("hello.bin")
    let man = try manifest()
    #expect(m.type == "hello")
    #expect(m.token == man["token_hash"] as? String)
    #expect(m.node_id == man["node_id"] as? String)
    #expect(m.name == "golden-mac")
    #expect(m.version == 1)
    #expect(m.protocol_major == 1)
    #expect(m.protocol_minor == 0)
    // Our own hashing of the golden token must equal Python's.
    #expect(sha256Hex(man["token"] as! String) == man["token_hash"] as? String)
}

@Test func goldenClipTextDecodes() throws {
    let m = try decodeGoldenFrame("clip_text.bin")
    let man = try manifest()
    #expect(m.kind == "text")
    #expect(m.content == man["text"] as? String)
    #expect(sha256Hex(m.content!) == man["text_hash"] as? String)
}

@Test func goldenClipImageDecodes() throws {
    let m = try decodeGoldenFrame("clip_image.bin")
    let man = try manifest()
    let data = strictBase64Decode(m.content!)
    #expect(data != nil)
    #expect(sha256Hex(data!) == man["image_hash"] as? String)
    #expect(m.bytes == data!.count)
    // Our base64 encoding round-trips to Python's exact string.
    #expect(data!.base64EncodedString() == man["image_b64"] as? String)
}

@Test func goldenClipFileDecodes() throws {
    let m = try decodeGoldenFrame("clip_file.bin")
    let man = try manifest()
    #expect(m.name == man["file_name"] as? String)
    let data = strictBase64Decode(m.content!)
    #expect(sha256Hex(data!) == man["file_hash"] as? String)
}

@Test func goldenPingDecodes() throws {
    let m = try decodeGoldenFrame("ping.bin")
    #expect(m.type == "ping")
    #expect(m.ts == 1718000000.5)
}

@Test func ourHelloDecodesLikePythonWould() throws {
    // Sanity: encode our hello and re-parse it with the same tolerant rules
    // Python uses (a dict lookup). Field names must be snake_case.
    let m = WireMessage.hello(tokenHash: "h", nodeID: "n", name: "x", appVersion: "1")
    let body = try JSONEncoder().encode(m)
    let dict = try JSONSerialization.jsonObject(with: body) as! [String: Any]
    for key in ["type", "token", "node_id", "name", "version",
                "app_version", "protocol_major", "protocol_minor"] {
        #expect(dict[key] != nil, "missing wire field \(key)")
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Expected: all PASS (fixtures load via `Bundle.module`).

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: golden wire vectors generated from Python encoding rules"
```

---

### Task 8: Core — TXT record codec + rotating file logger

**Files:**
- Create: `formacOS/Sources/AnyClipCore/TXTCodec.swift`
- Create: `formacOS/Sources/AnyClipCore/Logging.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/TXTCodecTests.swift`
- Test: `formacOS/Tests/AnyClipCoreTests/LoggingTests.swift`

Reference: `anyclip.py:304-341` (setup_logging: 5 MB × 3 backups, format `%(asctime)s %(levelname)s %(message)s`).

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipCoreTests/TXTCodecTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

@Test func txtRoundTrip() {
    let entries: [(String, String)] = [
        ("id", "11111111-2222-3333-4444-555555555555"),
        ("version", "1"),
        ("app_version", "0.0.0-dev"),
        ("protocol_major", "1"),
        ("protocol_minor", "0"),
    ]
    let data = TXTCodec.encode(entries)
    let decoded = TXTCodec.decode(data)
    #expect(decoded["id"] == "11111111-2222-3333-4444-555555555555")
    #expect(decoded["protocol_major"] == "1")
    #expect(decoded.count == 5)
}

@Test func txtEntriesAreLengthPrefixedKeyEqualsValue() {
    // DNS TXT wire format: 1 length byte, then "key=value" bytes.
    let data = TXTCodec.encode([("k", "v")])
    #expect(data == Data([3]) + Data("k=v".utf8))
}

@Test func txtDecodeIgnoresMalformedTail() {
    var data = TXTCodec.encode([("a", "1")])
    data.append(250) // length byte promising more than available
    data.append(Data("xx".utf8))
    #expect(TXTCodec.decode(data) == ["a": "1"])
}

@Test func txtSkipsOversizedEntries() {
    let big = String(repeating: "v", count: 300)
    let data = TXTCodec.encode([("big", big), ("ok", "1")])
    #expect(TXTCodec.decode(data) == ["ok": "1"])
}
```

`formacOS/Tests/AnyClipCoreTests/LoggingTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipCore

private func tempLogURL() -> URL {
    let dir = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-log-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir.appendingPathComponent("anyclip.log")
}

@Test func writesFormattedLines() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false)
    log.info("hello world")
    log.flushForTesting()
    let content = try String(contentsOf: url, encoding: .utf8)
    #expect(content.contains(" INFO hello world\n"))
    // "YYYY-MM-DD HH:MM:SS,mmm LEVEL msg" — same shape as Python logging.
    let prefix = content.prefix(23) // "2026-06-11 10:00:00,123"
    #expect(prefix.count == 23)
    #expect(prefix[prefix.index(prefix.startIndex, offsetBy: 4)] == "-")
    #expect(prefix[prefix.index(prefix.startIndex, offsetBy: 19)] == ",")
}

@Test func rotatesAtMaxBytes() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false, maxBytes: 200, backupCount: 3)
    for i in 0..<30 { log.info("line \(i) padding padding padding") }
    log.flushForTesting()
    let dir = url.deletingLastPathComponent()
    let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
    #expect(names.contains("anyclip.log"))
    #expect(names.contains("anyclip.log.1"))
    // never more than backupCount backups
    #expect(!names.contains("anyclip.log.4"))
    let mainSize = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as! Int
    #expect(mainSize <= 300) // freshly rotated file stays small
}

@Test func debugIsAlwaysInFileLog() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false)
    log.debug("dbg-marker")
    log.flushForTesting()
    let content = try String(contentsOf: url, encoding: .utf8)
    #expect(content.contains("DEBUG dbg-marker"))
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipCore/TXTCodec.swift`:

```swift
import Foundation

/// Minimal DNS TXT record codec (RFC 6763 §6): each entry is one length
/// byte followed by "key=value" bytes. Used to advertise/parse the same
/// properties the Python zeroconf beacon uses. Entries over 255 bytes are
/// skipped (ours are all tiny).
public enum TXTCodec {
    public static func encode(_ entries: [(String, String)]) -> Data {
        var out = Data()
        for (key, value) in entries {
            let raw = Data("\(key)=\(value)".utf8)
            guard raw.count <= 255 else { continue }
            out.append(UInt8(raw.count))
            out.append(raw)
        }
        return out
    }

    public static func decode(_ data: Data) -> [String: String] {
        var result: [String: String] = [:]
        var i = data.startIndex
        while i < data.endIndex {
            let len = Int(data[i])
            i = data.index(after: i)
            guard len > 0,
                  let end = data.index(i, offsetBy: len, limitedBy: data.endIndex)
            else { break }
            if let s = String(data: data[i..<end], encoding: .utf8),
               let eq = s.firstIndex(of: "=") {
                result[String(s[..<eq])] = String(s[s.index(after: eq)...])
            }
            i = end
        }
        return result
    }
}
```

`formacOS/Sources/AnyClipCore/Logging.swift`:

```swift
import Foundation

/// Rotating file logger writing the same line shape as Python's logging
/// ("YYYY-MM-DD HH:MM:SS,mmm LEVEL message"), into the same
/// ~/.anyclip/anyclip.log, 5 MB × 3 backups. File level is always DEBUG;
/// console (stderr) level respects `verbose`. Thread-safe via a serial queue.
public final class AnyLog: @unchecked Sendable {
    public static let shared = AnyLog()

    public enum Level: Int, Sendable {
        case debug = 10, info = 20, warning = 30, error = 40
        var label: String {
            switch self {
            case .debug: return "DEBUG"
            case .info: return "INFO"
            case .warning: return "WARNING"
            case .error: return "ERROR"
            }
        }
    }

    private let queue = DispatchQueue(label: "anyclip.log")
    private var fileURL: URL?
    private var handle: FileHandle?
    private var consoleLevel: Level = .info
    private var maxBytes = 5 * 1024 * 1024
    private var backupCount = 3
    private let formatter: DateFormatter

    public init() {
        formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd HH:mm:ss,SSS"
        formatter.locale = Locale(identifier: "en_US_POSIX")
    }

    public func configure(
        fileURL: URL, verbose: Bool,
        maxBytes: Int = 5 * 1024 * 1024, backupCount: Int = 3
    ) {
        queue.sync {
            self.consoleLevel = verbose ? .debug : .info
            self.maxBytes = maxBytes
            self.backupCount = backupCount
            self.fileURL = fileURL
            openHandle()
        }
    }

    public func debug(_ message: String) { write(.debug, message) }
    public func info(_ message: String) { write(.info, message) }
    public func warning(_ message: String) { write(.warning, message) }
    public func error(_ message: String) { write(.error, message) }

    /// Drains the queue so tests can read the file deterministically.
    public func flushForTesting() { queue.sync {} }

    private func write(_ level: Level, _ message: String) {
        queue.async { [self] in
            let line = "\(formatter.string(from: Date())) \(level.label) \(message)\n"
            let data = Data(line.utf8)
            if level.rawValue >= consoleLevel.rawValue {
                FileHandle.standardError.write(data)
            }
            guard let handle else { return }
            handle.write(data)
            rotateIfNeeded()
        }
    }

    private func openHandle() {
        guard let fileURL else { return }
        try? FileManager.default.createDirectory(
            at: fileURL.deletingLastPathComponent(), withIntermediateDirectories: true)
        if !FileManager.default.fileExists(atPath: fileURL.path) {
            FileManager.default.createFile(atPath: fileURL.path, contents: nil)
        }
        handle = try? FileHandle(forWritingTo: fileURL)
        _ = try? handle?.seekToEnd()
    }

    private func rotateIfNeeded() {
        guard let fileURL, let handle,
              let offset = try? handle.offset(), offset > UInt64(maxBytes)
        else { return }
        try? handle.close()
        self.handle = nil
        let fm = FileManager.default
        let base = fileURL.path
        try? fm.removeItem(atPath: "\(base).\(backupCount)")
        if backupCount >= 2 {
            for i in stride(from: backupCount - 1, through: 1, by: -1) {
                if fm.fileExists(atPath: "\(base).\(i)") {
                    try? fm.moveItem(atPath: "\(base).\(i)", toPath: "\(base).\(i + 1)")
                }
            }
        }
        try? fm.moveItem(atPath: base, toPath: "\(base).1")
        openHandle()
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: TXT record codec + rotating file logger"
```

---

### Task 9: Daemon — SystemInfo, FatalStartupError, PidLock

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/SystemInfo.swift`
- Create: `formacOS/Sources/AnyClipDaemon/PidLock.swift`
- Delete: `formacOS/Sources/AnyClipDaemon/Placeholder.swift`
- Modify: `formacOS/Tests/AnyClipDaemonTests/SmokeTests.swift` (drop marker reference)
- Test: `formacOS/Tests/AnyClipDaemonTests/PidLockTests.swift`

Reference: `anyclip.py:134-283` (FatalStartupError, pid helpers, prepare/release), `anyclip.py:614-623` (get_local_ipv4).

- [ ] **Step 1: Write failing tests**

Replace `formacOS/Tests/AnyClipDaemonTests/SmokeTests.swift` with:

```swift
import Testing
@testable import AnyClipDaemon

@Test func primaryIPv4LooksLikeDottedQuad() {
    // Best-effort: on a machine with no network this returns nil; the
    // assertion only runs when an address exists.
    if let ip = primaryIPv4() {
        let parts = ip.split(separator: ".")
        #expect(parts.count == 4)
        #expect(parts.allSatisfy { UInt8($0) != nil })
    }
}

@Test func monotonicNowAdvances() {
    let a = monotonicNow()
    let b = monotonicNow()
    #expect(b >= a)
}
```

`formacOS/Tests/AnyClipDaemonTests/PidLockTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipDaemon

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-pid-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func prepareWritesOwnPidAndPort() throws {
    let dir = tempDir()
    try PidLock.prepare(port: 24816, dir: dir)
    let content = try String(contentsOf: dir.appendingPathComponent("anyclip.pid"),
                             encoding: .utf8)
    #expect(content == "\(getpid()) 24816\n")
    PidLock.release(dir: dir)
    #expect(!FileManager.default.fileExists(
        atPath: dir.appendingPathComponent("anyclip.pid").path))
}

@Test func staleDeadPidIsOverwritten() throws {
    let dir = tempDir()
    // PID 1 is launchd: alive but is NOT an anyclip process — however the
    // PID-file path only checks liveness+terminate, so use an impossible pid.
    try "999999 24816\n".write(to: dir.appendingPathComponent("anyclip.pid"),
                               atomically: true, encoding: .utf8)
    try PidLock.prepare(port: 24816, dir: dir)
    let content = try String(contentsOf: dir.appendingPathComponent("anyclip.pid"),
                             encoding: .utf8)
    #expect(content.hasPrefix("\(getpid()) "))
    PidLock.release(dir: dir)
}

@Test func releaseLeavesForeignPidFileAlone() throws {
    let dir = tempDir()
    try "999999 24816\n".write(to: dir.appendingPathComponent("anyclip.pid"),
                               atomically: true, encoding: .utf8)
    PidLock.release(dir: dir)
    #expect(FileManager.default.fileExists(
        atPath: dir.appendingPathComponent("anyclip.pid").path))
}

@Test func isAnyclipPidMatchesCaseInsensitively() {
    // Our own test process path contains neither; assert the pure matcher.
    #expect(PidLock.argsLookLikeAnyclip("/Applications/AnyClip.app/Contents/MacOS/AnyClip"))
    #expect(PidLock.argsLookLikeAnyclip("python3 /Users/x/AnyClip/anyclip.py --headless"))
    #expect(!PidLock.argsLookLikeAnyclip("/usr/bin/nc -l 24816"))
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/SystemInfo.swift`:

```swift
import Foundation

/// Best-effort primary IPv4 of this host: the source IP for the default
/// route, discovered by "connecting" a UDP socket to 8.8.8.8:80 (no packet
/// is sent). Port of anyclip.get_local_ipv4().
public func primaryIPv4() -> String? {
    let fd = socket(AF_INET, SOCK_DGRAM, 0)
    guard fd >= 0 else { return nil }
    defer { close(fd) }
    var addr = sockaddr_in()
    addr.sin_family = sa_family_t(AF_INET)
    addr.sin_port = in_port_t(80).bigEndian
    guard inet_pton(AF_INET, "8.8.8.8", &addr.sin_addr) == 1 else { return nil }
    let rc = withUnsafePointer(to: &addr) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
            connect(fd, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
        }
    }
    guard rc == 0 else { return nil }
    var local = sockaddr_in()
    var len = socklen_t(MemoryLayout<sockaddr_in>.size)
    let rc2 = withUnsafeMutablePointer(to: &local) {
        $0.withMemoryRebound(to: sockaddr.self, capacity: 1) {
            getsockname(fd, $0, &len)
        }
    }
    guard rc2 == 0 else { return nil }
    var buf = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
    var sin = local.sin_addr
    guard inet_ntop(AF_INET, &sin, &buf, socklen_t(INET_ADDRSTRLEN)) != nil else { return nil }
    return String(cString: buf)
}

/// Monotonic seconds (never goes backwards on clock changes).
public func monotonicNow() -> Double {
    Double(DispatchTime.now().uptimeNanoseconds) / 1_000_000_000
}

/// Raised when the daemon cannot start and retrying will not help.
/// The in-process supervisor recognises this and stops instead of looping.
public struct FatalStartupError: Error, CustomStringConvertible {
    public let message: String
    public var description: String { message }
    public init(_ message: String) { self.message = message }
}
```

`formacOS/Sources/AnyClipDaemon/PidLock.swift`:

```swift
import Foundation
import AnyClipCore

/// Single-instance lock shared with the Python implementation
/// (~/.anyclip/anyclip.pid, "<pid> <port>\n"). Port of
/// anyclip.prepare_pid_lock / release_pid_lock and helpers.
public enum PidLock {
    public static func prepare(port: UInt16, dir: URL) throws {
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let pidFile = dir.appendingPathComponent("anyclip.pid")

        // 1) PID file from a previous run (Python or Swift).
        if let content = try? String(contentsOf: pidFile, encoding: .utf8),
           let first = content.split(separator: " ").first,
           let oldPid = Int32(first.trimmingCharacters(in: .whitespacesAndNewlines)),
           oldPid > 0, oldPid != getpid(), processAlive(oldPid) {
            AnyLog.shared.info("another anyclip detected (pid \(oldPid) via PID file); terminating")
            guard terminate(oldPid) else {
                throw FatalStartupError(
                    "could not terminate previous anyclip (pid \(oldPid)); "
                    + "please run: kill -9 \(oldPid)")
            }
            AnyLog.shared.info("previous anyclip (pid \(oldPid)) terminated")
        }

        // 2) Stale state: port held without a matching PID file.
        if let listenerPid = findListeningPid(port: port), listenerPid != getpid() {
            if isAnyclipPid(listenerPid) {
                AnyLog.shared.info("anyclip listening on tcp/\(port) (pid \(listenerPid)); terminating")
                guard terminate(listenerPid) else {
                    throw FatalStartupError(
                        "could not terminate anyclip on tcp/\(port) (pid \(listenerPid)); "
                        + "please run: kill -9 \(listenerPid)")
                }
                usleep(300_000) // let the OS release the socket
            } else {
                throw FatalStartupError(
                    "tcp/\(port) is held by a non-anyclip process (pid \(listenerPid)); "
                    + "stop that process or quit it first")
            }
        }

        // 3) Record our pid (and chosen port for diagnostics).
        try? "\(getpid()) \(port)\n".write(to: pidFile, atomically: true, encoding: .utf8)
    }

    /// Remove our PID file, but only if it still points at us.
    public static func release(dir: URL) {
        let pidFile = dir.appendingPathComponent("anyclip.pid")
        guard let content = try? String(contentsOf: pidFile, encoding: .utf8),
              let first = content.split(separator: " ").first,
              Int32(first.trimmingCharacters(in: .whitespacesAndNewlines)) == getpid()
        else { return }
        try? FileManager.default.removeItem(at: pidFile)
    }

    static func processAlive(_ pid: Int32) -> Bool {
        guard pid > 0 else { return false }
        if kill(pid, 0) == 0 { return true }
        return errno == EPERM // exists, owned by another user
    }

    /// Pure matcher, exposed for tests. Case-insensitive so it recognises
    /// both `anyclip.py` and `AnyClip.app` command lines.
    static func argsLookLikeAnyclip(_ args: String) -> Bool {
        args.lowercased().contains("anyclip")
    }

    static func isAnyclipPid(_ pid: Int32) -> Bool {
        guard let out = runCommand("/bin/ps", ["-p", "\(pid)", "-o", "args="]) else {
            return false
        }
        return argsLookLikeAnyclip(out)
    }

    static func findListeningPid(port: UInt16) -> Int32? {
        guard let out = runCommand(
            "/usr/sbin/lsof", ["-nP", "-iTCP:\(port)", "-sTCP:LISTEN", "-t"])
        else { return nil }
        for line in out.split(separator: "\n") {
            if let pid = Int32(line.trimmingCharacters(in: .whitespaces)) { return pid }
        }
        return nil
    }

    /// SIGTERM, wait up to 2 s, then SIGKILL. True if the pid is gone.
    static func terminate(_ pid: Int32) -> Bool {
        if kill(pid, SIGTERM) != 0, !processAlive(pid) { return true }
        for _ in 0..<20 {
            usleep(100_000)
            if !processAlive(pid) { return true }
        }
        kill(pid, SIGKILL)
        for _ in 0..<10 {
            usleep(100_000)
            if !processAlive(pid) { return true }
        }
        return !processAlive(pid)
    }

    private static func runCommand(_ path: String, _ args: [String]) -> String? {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: path)
        process.arguments = args
        let pipe = Pipe()
        process.standardOutput = pipe
        process.standardError = FileHandle.nullDevice
        do { try process.run() } catch { return nil }
        let data = pipe.fileHandleForReading.readDataToEndOfFile()
        process.waitUntilExit()
        return String(data: data, encoding: .utf8)
    }
}
```

Delete `formacOS/Sources/AnyClipDaemon/Placeholder.swift`.

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add -A formacOS
git commit -m "formacOS: pid lock + primary-IPv4 + fatal startup error"
```

---

### Task 10: Daemon — FramedConnection + withTimeout (loopback-tested)

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/FramedConnection.swift`
- Create: `formacOS/Sources/AnyClipDaemon/Timeout.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/FramedConnectionTests.swift`

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/FramedConnectionTests.swift`:

```swift
import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

/// Loopback NWListener that hands its first inbound connection to the test.
private func startLoopbackListener(
    port: UInt16, onConnection: @escaping (NWConnection) -> Void
) throws -> NWListener {
    let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    listener.newConnectionHandler = onConnection
    listener.start(queue: .global())
    return listener
}

@Test func sendAndReceiveFrameOverLoopback() async throws {
    let port: UInt16 = 28461
    let inbound = Locked<FramedConnection?>(nil)
    let listener = try startLoopbackListener(port: port) { conn in
        let framed = FramedConnection(connection: conn)
        conn.start(queue: .global())
        inbound.set(framed)
    }
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }

    try await client.sendFrame(.clipText("ping-pong", ts: 1))
    // Wait for the listener side to appear, then read the frame there.
    var server: FramedConnection?
    for _ in 0..<100 {
        if let s = inbound.get() { server = s; break }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    let received = try await server!.receiveMessage()
    #expect(received?.type == "clip")
    #expect(received?.content == "ping-pong")
    server?.cancel()
}

@Test func eofSurfacesAsConnectionClosed() async throws {
    let port: UInt16 = 28462
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global())
        // Close immediately after accept.
        conn.cancel()
    }
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }
    await #expect(throws: (any Error).self) {
        _ = try await client.receiveMessage()
    }
}

@Test func withTimeoutThrowsOnSlowOperation() async throws {
    await #expect(throws: TimeoutError.self) {
        try await withTimeout(seconds: 0.05) {
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
    }
}

@Test func withTimeoutPassesThroughFastResult() async throws {
    let v = try await withTimeout(seconds: 1.0) { 42 }
    #expect(v == 42)
}

@Test func remoteIPIsCapturedOnReady() async throws {
    let port: UInt16 = 28463
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global())
    }
    defer { listener.cancel() }
    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }
    #expect(client.remoteIP == "127.0.0.1")
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/Timeout.swift`:

```swift
import Foundation

public struct TimeoutError: Error, Equatable {}

/// Race `operation` against a deadline. NOTE: if the operation is stuck in
/// a non-cancellable continuation (NWConnection receive), the loser task
/// only ends when the caller cancels the underlying connection — always
/// cancel the connection after catching TimeoutError.
public func withTimeout<T: Sendable>(
    seconds: Double,
    operation: @escaping @Sendable () async throws -> T
) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            throw TimeoutError()
        }
        let result = try await group.next()!
        group.cancelAll()
        return result
    }
}

/// Tiny thread-safe box used by connection callbacks and tests.
public final class Locked<T>: @unchecked Sendable {
    private var value: T
    private let lock = NSLock()
    public init(_ initial: T) { value = initial }
    public func get() -> T { lock.lock(); defer { lock.unlock() }; return value }
    public func set(_ new: T) { lock.lock(); defer { lock.unlock() }; value = new }
    public func exchange(_ new: T) -> T {
        lock.lock(); defer { lock.unlock() }
        let old = value; value = new; return old
    }
}
```

`formacOS/Sources/AnyClipDaemon/FramedConnection.swift`:

```swift
import Foundation
import Network
import AnyClipCore

public enum WireConnectionError: Error {
    case closed
    case cancelled
}

/// Async framing layer over one NWConnection: 4-byte BE length + JSON body,
/// mirroring PeerLink._send/_recv in anyclip.py.
public final class FramedConnection: @unchecked Sendable {
    public let connection: NWConnection
    public private(set) var remoteIP: String?

    public init(connection: NWConnection) {
        self.connection = connection
    }

    /// Outbound connection with the same TCP tuning as the Python client:
    /// keepalive on (idle 15 s) and a 5 s connect timeout.
    public static func outbound(to endpoint: NWEndpoint) -> FramedConnection {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
        tcp.connectionTimeout = 5
        let params = NWParameters(tls: nil, tcp: tcp)
        return FramedConnection(connection: NWConnection(to: endpoint, using: params))
    }

    /// Start and suspend until .ready (throws on .failed/.cancelled).
    public func start() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let resumed = Locked(false)
            connection.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    self?.captureRemoteIP()
                    if !resumed.exchange(true) { cont.resume() }
                case .failed(let error):
                    if !resumed.exchange(true) { cont.resume(throwing: error) }
                case .cancelled:
                    if !resumed.exchange(true) {
                        cont.resume(throwing: WireConnectionError.cancelled)
                    }
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .userInitiated))
        }
        connection.stateUpdateHandler = nil
    }

    private func captureRemoteIP() {
        guard case let .hostPort(host, _)? = connection.currentPath?.remoteEndpoint
        else { return }
        // Host description can carry a scope suffix ("192.168.0.5%en0").
        remoteIP = "\(host)".split(separator: "%").first.map(String.init)
    }

    /// For inbound connections the caller starts NWConnection itself; this
    /// waits for readiness the same way.
    public static func inbound(_ connection: NWConnection) -> FramedConnection {
        FramedConnection(connection: connection)
    }

    public func sendFrame(_ message: WireMessage) async throws {
        let data = try message.encodeFrame()
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            connection.send(content: data, completion: .contentProcessed { error in
                if let error { cont.resume(throwing: error) } else { cont.resume() }
            })
        }
    }

    /// One message, or nil on an invalid frame (bad length / bad JSON) —
    /// the caller closes the session on nil, matching Python _recv().
    public func receiveMessage() async throws -> WireMessage? {
        let header = try await receiveExactly(4)
        let n = WireMessage.frameLength(header)
        guard n > 0, n <= Wire.maxPayload else {
            AnyLog.shared.warning("invalid frame length: \(n)")
            return nil
        }
        let body = try await receiveExactly(n)
        let msg = WireMessage.decodeBody(body)
        if msg == nil { AnyLog.shared.warning("bad json frame (\(n) bytes)") }
        return msg
    }

    private func receiveExactly(_ n: Int) async throws -> Data {
        var buffer = Data()
        while buffer.count < n {
            let chunk = try await receiveSome(max: n - buffer.count)
            buffer.append(chunk)
        }
        return buffer
    }

    private func receiveSome(max: Int) async throws -> Data {
        try await withCheckedThrowingContinuation { cont in
            connection.receive(minimumIncompleteLength: 1, maximumLength: max) {
                content, _, isComplete, error in
                if let error { cont.resume(throwing: error); return }
                if let content, !content.isEmpty {
                    cont.resume(returning: content)
                    return
                }
                cont.resume(throwing: WireConnectionError.closed) // EOF
            }
        }
    }

    public func cancel() {
        connection.cancel()
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Note: loopback tests may trigger a one-time Local Network prompt; they use 127.0.0.1 so normally none appears.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: framed NWConnection wrapper + timeout helper"
```

---

### Task 11: Daemon — PeerLink actor

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/PeerLink.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift`

Reference: `anyclip.py:1086-1511` (PeerLink). The test links **two Swift PeerLinks** over loopback — handshake, clip exchange, auth rejection.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/PeerLinkTests.swift`:

```swift
import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeLink(
    token: String, port: UInt16, name: String,
    clips: Locked<[ClipPayload]>, events: Locked<[DaemonEvent]>
) async -> PeerLink {
    let link = PeerLink(
        config: PeerLink.LinkConfig(
            token: token, port: port, name: name, appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(
        onClip: { payload in clips.set(clips.get() + [payload]) },
        emit: { event in events.set(events.get() + [event]) })
    return link
}

private func waitUntil(
    _ timeout: Double = 5.0, _ cond: @escaping () async -> Bool
) async -> Bool {
    let deadline = monotonicNow() + timeout
    while monotonicNow() < deadline {
        if await cond() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return await cond()
}

@Test func twoLinksHandshakeAndExchangeClips() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28471, name: "node-a",
                           clips: aClips, events: aEvents)
    let b = await makeLink(token: "tok", port: 28472, name: "node-b",
                           clips: bClips, events: bEvents)

    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let connectB = Task {
        await b.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28471)!),
            label: "127.0.0.1:28471")
    }
    defer { connectB.cancel() }

    #expect(await waitUntil { (await a.isActive) && (await b.isActive) })
    #expect(await a.peerName == "node-b")
    #expect(await b.peerName == "node-a")
    #expect(aEvents.get().contains { if case .linkUp = $0 { return true }; return false })

    await b.sendClip(.text("from-b"))
    #expect(await waitUntil {
        aClips.get().contains {
            if case .text(let s) = $0 { return s == "from-b" }
            return false
        }
    })

    await a.sendClip(.image(Data([1, 2, 3])))
    #expect(await waitUntil {
        bClips.get().contains {
            if case .image(let d) = $0 { return d == Data([1, 2, 3]) }
            return false
        }
    })

    await a.shutdown(); await b.shutdown()
}

@Test func wrongTokenIsRejectedWithAuthEvent() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "right", port: 28473, name: "a",
                           clips: aClips, events: aEvents)
    let b = await makeLink(token: "wrong", port: 28474, name: "b",
                           clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    await b.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28473)!),
        label: "127.0.0.1:28473")

    #expect(await waitUntil {
        aEvents.get().contains {
            if case .handshakeFailed(_, "auth") = $0 { return true }; return false
        }
    })
    #expect(!(await a.isActive))
    await a.shutdown(); await b.shutdown()
}

@Test func pingIsAnsweredWithPong() async throws {
    // Drive a raw FramedConnection against a serving PeerLink: complete the
    // handshake manually, send ping, expect pong.
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28475, name: "a",
                           clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28475)!))
    try await raw.start()
    defer { raw.cancel() }
    try await raw.sendFrame(.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-raw", name: "raw",
        appVersion: "0.0.0-test"))
    let serverHello = try await raw.receiveMessage()
    #expect(serverHello?.type == "hello")
    try await raw.sendFrame(.ping(ts: 1))
    let reply = try await raw.receiveMessage()
    #expect(reply?.type == "pong")
    await a.shutdown()
}

@Test func majorVersionMismatchIsRefused() async throws {
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28476, name: "a",
                           clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28476)!))
    try await raw.start()
    defer { raw.cancel() }
    var hello = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-v2", name: "future",
        appVersion: "2.0.0")
    hello.protocol_major = 2
    try await raw.sendFrame(hello)
    _ = try await raw.receiveMessage() // server's hello
    #expect(await waitUntil {
        events.get().contains {
            if case .handshakeFailed(_, let r) = $0 { return r.hasPrefix("version:") }
            return false
        }
    })
    #expect(!(await a.isActive))
    await a.shutdown()
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/PeerLink.swift`:

```swift
import Foundation
import Network
import AnyClipCore

/// Owns the single active TCP link to a peer. Acts as both server and
/// client; resolves the simultaneous-connect race via lexicographic
/// node_id tie-break. Port of anyclip.PeerLink — actor isolation replaces
/// the asyncio Lock; there are no awaits inside the registration block, so
/// it is atomic exactly like the Python critical section.
public actor PeerLink {
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

    private let config: LinkConfig
    private let nodeID: String
    private let tokenHash: String
    private var authGate: AuthGate
    private var onClip: (@Sendable (ClipPayload) async -> Void)?
    private var emit: (@Sendable (DaemonEvent) -> Void)?

    private var activeConn: FramedConnection?
    private var peerNodeID: String?
    public private(set) var peerName: String?
    private var linkedAt: Double = 0
    private var connecting: Set<String> = []

    private var listener: NWListener?
    public private(set) var isServing = false
    private var advertiseService: NWListener.Service?

    public init(config: LinkConfig, nodeID: String) {
        self.config = config
        self.nodeID = nodeID
        self.tokenHash = sha256Hex(config.token)
        self.authGate = AuthGate()
    }

    public var isActive: Bool { activeConn != nil }

    public func setHandlers(
        onClip: @escaping @Sendable (ClipPayload) async -> Void,
        emit: @escaping @Sendable (DaemonEvent) -> Void
    ) {
        self.onClip = onClip
        self.emit = emit
    }

    /// Bonjour advertisement carried by the TCP listener. Must be called
    /// before serve(). instanceName is "{name}-{nodeId8}".
    public func configureAdvertising(instanceName: String, txtData: Data) {
        advertiseService = NWListener.Service(
            name: instanceName, type: Wire.serviceType, domain: nil, txtRecord: txtData)
    }

    /// Re-publish the Bonjour advertisement (mDNS self-heal).
    public func reAnnounce() {
        guard let listener, let advertiseService else { return }
        listener.service = nil
        listener.service = advertiseService
        AnyLog.shared.debug("mDNS: re-announced service")
    }

    public func serve() async throws {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
        let params = NWParameters(tls: nil, tcp: tcp)
        params.allowLocalEndpointReuse = true
        let listener: NWListener
        do {
            listener = try NWListener(
                using: params, on: NWEndpoint.Port(rawValue: config.port)!)
        } catch {
            throw FatalStartupError("could not open tcp/\(config.port): \(error)")
        }
        listener.service = advertiseService
        listener.newConnectionHandler = { [weak self] conn in
            guard let self else { conn.cancel(); return }
            Task { await self.handleInbound(conn) }
        }
        self.listener = listener

        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let resumed = Locked(false)
            listener.stateUpdateHandler = { state in
                switch state {
                case .ready:
                    if !resumed.exchange(true) { cont.resume() }
                case .failed(let error):
                    if !resumed.exchange(true) {
                        if case .posix(.EADDRINUSE) = error {
                            cont.resume(throwing: FatalStartupError(
                                "port \(self.config.port) still in use after cleanup attempt; "
                                + "another process may have grabbed it"))
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
            listener.start(queue: .global(qos: .userInitiated))
        }
        listener.stateUpdateHandler = nil
        isServing = true
        AnyLog.shared.info("listening on tcp/\(config.port)")
        // Park until cancelled; inbound sessions run in their own Tasks.
        while true {
            try await Task.sleep(nanoseconds: 1_000_000_000)
        }
    }

    private func handleInbound(_ conn: NWConnection) async {
        let framed = FramedConnection(connection: conn)
        do {
            try await framed.start()
        } catch {
            framed.cancel()
            return
        }
        AnyLog.shared.debug("inbound from \(framed.remoteIP ?? "?")")
        if let ip = framed.remoteIP, authGate.isBlocked(ip) {
            AnyLog.shared.info(
                "auth gate: \(ip) blocked (>\(AuthGate.maxFails) failures, "
                + "cooldown \(Int(AuthGate.cooldown))s)")
            framed.cancel()
            return
        }
        await session(framed, inbound: true)
        framed.cancel()
    }

    public func tryConnect(to endpoint: NWEndpoint, label: String) async {
        if isActive { return }
        if connecting.contains(label) {
            AnyLog.shared.debug("connect to \(label) already in flight, skipping")
            return
        }
        connecting.insert(label)
        defer { connecting.remove(label) }
        let framed = FramedConnection.outbound(to: endpoint)
        do {
            try await withTimeout(seconds: Wire.connectTimeout) {
                try await framed.start()
            }
        } catch {
            AnyLog.shared.info("connect to \(label) failed: \(error)")
            framed.cancel()
            return
        }
        AnyLog.shared.debug("outbound connected to \(label)")
        await session(framed, inbound: false)
        framed.cancel()
    }

    private func session(_ framed: FramedConnection, inbound: Bool) async {
        let myHello = WireMessage.hello(
            tokenHash: tokenHash, nodeID: nodeID,
            name: config.name, appVersion: config.appVersion)
        do {
            try await framed.sendFrame(myHello)
        } catch {
            return
        }
        let addr = framed.remoteIP ?? ""

        let peerHello: WireMessage?
        do {
            peerHello = try await withTimeout(seconds: Wire.handshakeTimeout) {
                try await framed.receiveMessage()
            }
        } catch is TimeoutError {
            AnyLog.shared.warning("handshake timeout")
            emit?(.handshakeFailed(addr: addr, reason: "timeout"))
            framed.cancel()
            return
        } catch {
            return
        }
        guard let hello = peerHello, hello.type == "hello" else {
            AnyLog.shared.warning("invalid hello, closing")
            emit?(.handshakeFailed(addr: addr, reason: "invalid"))
            return
        }
        let peerIP = inbound ? framed.remoteIP : nil
        guard hello.token == tokenHash else {
            AnyLog.shared.warning("auth failed from peer name=\(hello.name ?? "?")")
            if let ip = peerIP { authGate.recordFail(ip) }
            emit?(.handshakeFailed(addr: peerIP ?? addr, reason: "auth"))
            return
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
            return
        }
        if compat != .compatible {
            AnyLog.shared.info("version mismatch (link kept): \(compat.rawValue)")
        }
        guard let peerID = hello.node_id, peerID != nodeID else {
            AnyLog.shared.debug("self loopback or bad node_id, dropping")
            return
        }
        if let ip = peerIP { authGate.recordOK(ip) }

        // Registration / tie-break. No awaits in this block — atomic.
        if activeConn != nil {
            let race = (monotonicNow() - linkedAt) < Wire.raceWindow
            if race {
                let keepThisLink =
                    (!inbound && nodeID < peerID) || (inbound && nodeID > peerID)
                if !keepThisLink {
                    AnyLog.shared.debug("tie-breaker: dropping duplicate link (race)")
                    return
                }
                AnyLog.shared.debug("tie-breaker: replacing existing link (race)")
            } else {
                AnyLog.shared.info(
                    "tie-breaker: stale link to \(peerName ?? "?") replaced by "
                    + "fresh handshake from \(hello.name ?? "?")")
            }
            activeConn?.cancel()
        }
        activeConn = framed
        peerNodeID = peerID
        let displayName = hello.name ?? String(peerID.prefix(8))
        peerName = displayName
        linkedAt = monotonicNow()
        AnyLog.shared.info(
            "linked with peer name=\(displayName) id=\(peerID.prefix(8)) "
            + "(\(inbound ? "inbound" : "outbound")) "
            + "peer_app_version=\(peerVersion.appVersion) "
            + "peer_proto=\(peerVersion.protocolMajor).\(peerVersion.protocolMinor)")
        emit?(.linkUp(peerName: displayName, peerID: peerID))

        // Receive loop.
        while true {
            let msg: WireMessage?
            do {
                msg = try await framed.receiveMessage()
            } catch {
                break
            }
            guard let m = msg else { break }
            switch m.type {
            case "clip":
                await handleClip(m)
            case "ping":
                try? await framed.sendFrame(
                    .pong(ts: Date().timeIntervalSince1970))
            case "pong":
                break // presence is enough
            default:
                AnyLog.shared.debug("ignoring message type: \(m.type)")
            }
        }

        let wasActive = (activeConn === framed)
        if wasActive {
            activeConn = nil
            peerNodeID = nil
            peerName = nil
        }
        AnyLog.shared.info("peer disconnected")
        if wasActive {
            emit?(.linkDown(reason: "peer disconnected"))
        }
    }

    private func handleClip(_ m: WireMessage) async {
        let kind = m.kind ?? "text"
        switch kind {
        case "text":
            if let content = m.content {
                await onClip?(.text(content))
            }
        case "image":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad image payload from peer")
                return
            }
            await onClip?(.image(data))
        case "file":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad file payload from peer")
                return
            }
            let name = (m.name?.isEmpty == false) ? m.name! : "received.bin"
            await onClip?(.file(name: name, data: data))
        default:
            AnyLog.shared.debug("ignoring clip with kind=\(kind)")
        }
    }

    /// App-layer keepalive frame; drives traffic so a silently-dead TCP
    /// socket surfaces as a send failure + EOF.
    public func sendPing() async {
        guard let conn = activeConn else { return }
        try? await conn.sendFrame(.ping(ts: Date().timeIntervalSince1970))
    }

    public func sendClip(_ payload: ClipPayload) async {
        guard let conn = activeConn else { return }
        let msg = WireMessage.clip(payload, ts: Date().timeIntervalSince1970)
        do {
            try await conn.sendFrame(msg)
        } catch let error as WireFrameError {
            AnyLog.shared.warning("payload too large, dropping: \(error)")
        } catch {
            AnyLog.shared.info("send failed (link likely down): \(error)")
        }
    }

    /// Drop the link + listener. Safe to call multiple times.
    public func shutdown() {
        activeConn?.cancel()
        activeConn = nil
        peerNodeID = nil
        peerName = nil
        listener?.cancel()
        listener = nil
        isServing = false
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Expected: all PASS. These are real-socket tests; if a port collides locally, change the test ports (2847x range) and re-run.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: PeerLink actor (handshake, tie-break, auth gate, clip exchange)"
```

---

### Task 12: Daemon — MdnsBeacon (browse + known-peers bookkeeping)

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/MdnsBeacon.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/MdnsBeaconTests.swift`

Reference: `anyclip.py:1514-1676`. Advertising lives on PeerLink's NWListener
(Task 11); the beacon owns browsing, `knownPeers`, `addressFails`,
`eventsSeen`, `advertisedIP`. Live-network browsing is verified in Task 19's
manual checklist; unit tests cover the pure bookkeeping.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/MdnsBeaconTests.swift`:

```swift
import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeBeacon(nodeID: String = "self-node") -> MdnsBeacon {
    MdnsBeacon(nodeID: nodeID, emit: { _ in }, onPeer: { _, _ in })
}

@Test func selfAdvertisementIsIgnored() async {
    let beacon = makeBeacon(nodeID: "self-node")
    await beacon.ingest(txt: ["id": "self-node"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "x")
    #expect(await beacon.eventsSeen == 0)
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func nonSelfPeerIsRecordedAndCountsAsEvidence() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "other-node"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "peer-1")
    #expect(await beacon.eventsSeen == 1)
    let peers = await beacon.peersSnapshot()
    #expect(peers.count == 1)
    #expect(peers[0].label == "peer-1")
}

@Test func missingTXTIdIsIgnored() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: [:], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "x")
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func freshDiscoveryClearsFailureCount() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "p"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    _ = await beacon.recordFail(label: "addr")
    _ = await beacon.recordFail(label: "addr")
    await beacon.ingest(txt: ["id": "p"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    #expect(await beacon.recordFail(label: "addr") == 1) // counter was reset
}

@Test func pruneRemovesAllNodeIdsForAddress() async {
    let beacon = makeBeacon()
    // Same address seen under two node ids (peer restarted, new uuid).
    await beacon.ingest(txt: ["id": "p1"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p2"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    #expect(await beacon.peersSnapshot().count == 1) // deduped by label
    await beacon.pruneAddress(label: "addr")
    #expect(await beacon.peersSnapshot().isEmpty)
}

@Test func snapshotDedupsByAddressLabel() async {
    let beacon = makeBeacon()
    await beacon.ingest(txt: ["id": "p1"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p2"], endpoint: .hostPort(host: "1.2.3.4", port: 24816), label: "addr")
    await beacon.ingest(txt: ["id": "p3"], endpoint: .hostPort(host: "5.6.7.8", port: 24816), label: "addr2")
    #expect(await beacon.peersSnapshot().count == 2)
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/MdnsBeacon.swift`:

```swift
import Foundation
import Network
import AnyClipCore

/// Browses for AnyClip peers on the LAN and keeps the reconnect bookkeeping
/// (knownPeers / addressFails / eventsSeen). Advertising itself is carried
/// by PeerLink's NWListener (configureAdvertising). Port of
/// anyclip.MdnsBeacon minus the zeroconf advertise half.
public actor MdnsBeacon {
    private let nodeID: String
    private let emit: @Sendable (DaemonEvent) -> Void
    private let onPeer: @Sendable (NWEndpoint, String) async -> Void

    private var browser: NWBrowser?
    /// peer node id -> (endpoint, label). A restarting peer mints a new
    /// node_id but keeps its address, so prune works on labels.
    private var knownPeers: [String: (endpoint: NWEndpoint, label: String)] = [:]
    private var addressFails: [String: Int] = [:]
    /// Non-self resolutions; consumed by the permission probe.
    public private(set) var eventsSeen = 0
    public private(set) var advertisedIP: String?

    public init(
        nodeID: String,
        emit: @escaping @Sendable (DaemonEvent) -> Void,
        onPeer: @escaping @Sendable (NWEndpoint, String) async -> Void
    ) {
        self.nodeID = nodeID
        self.emit = emit
        self.onPeer = onPeer
    }

    public func start() {
        advertisedIP = primaryIPv4()
        startBrowser()
    }

    private func startBrowser() {
        let browser = NWBrowser(
            for: .bonjourWithTXTRecord(type: Wire.serviceType, domain: nil),
            using: NWParameters())
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            Task { await self.handleResults(results) }
        }
        browser.start(queue: .global(qos: .utility))
        self.browser = browser
    }

    private func handleResults(_ results: Set<NWBrowser.Result>) async {
        for result in results {
            guard case .bonjour(let txtRecord) = result.metadata else { continue }
            var txt: [String: String] = [:]
            for (key, _) in txtRecord {
                if let value = txtRecord[key] { txt[key] = value }
            }
            let label = endpointLabel(result.endpoint)
            await ingest(txt: txt, endpoint: result.endpoint, label: label)
        }
    }

    /// Pure-ish ingestion of one resolved advertisement; exposed for tests.
    public func ingest(txt: [String: String], endpoint: NWEndpoint, label: String) async {
        guard let peerID = txt["id"] else { return }
        // Self-loopback discovery does not prove the network is alive.
        guard peerID != nodeID else { return }
        eventsSeen += 1
        knownPeers[peerID] = (endpoint, label)
        addressFails[label] = nil
        AnyLog.shared.info("discovered peer \(label)")
        emit(.peerDiscovered(name: label, addr: label))
        await onPeer(endpoint, label)
    }

    private func endpointLabel(_ endpoint: NWEndpoint) -> String {
        if case .service(let name, _, _, _) = endpoint { return name }
        return "\(endpoint)"
    }

    /// Re-issue the browse query (mDNS self-heal). The matching service
    /// re-announce lives on PeerLink.reAnnounce().
    public func refresh() {
        browser?.cancel()
        startBrowser()
        AnyLog.shared.debug("mDNS: browser re-issued")
    }

    public func stop() {
        browser?.cancel()
        browser = nil
    }

    // ---- reconnect-loop bookkeeping ------------------------------------

    /// Known peers deduped by address label (a restarted remote daemon
    /// leaves several stale node ids behind for the same address).
    public func peersSnapshot() -> [(endpoint: NWEndpoint, label: String)] {
        var seen = Set<String>()
        var out: [(endpoint: NWEndpoint, label: String)] = []
        for (_, value) in knownPeers where !seen.contains(value.label) {
            seen.insert(value.label)
            out.append(value)
        }
        return out
    }

    /// Returns the new consecutive-failure count for this address.
    public func recordFail(label: String) -> Int {
        let fails = (addressFails[label] ?? 0) + 1
        addressFails[label] = fails
        return fails
    }

    public func clearFails(label: String) {
        addressFails[label] = nil
    }

    public func pruneAddress(label: String) {
        knownPeers = knownPeers.filter { $0.value.label != label }
        addressFails[label] = nil
    }
}
```

> NWTXTRecord iteration note: `for (key, _) in txtRecord` + `txtRecord[key]`
> uses NWTXTRecord's Sequence + String subscript. If the toolchain rejects
> the Sequence conformance, replace the loop with
> `for key in ["id", "version", "app_version", "protocol_major", "protocol_minor"] { if let v = txtRecord[key] { txt[key] = v } }`
> — only `id` is consumed today.

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: mDNS beacon (browse + reconnect bookkeeping)"
```

---

### Task 13: Daemon — ClipboardWatcher

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift`

Reference: `anyclip.py:808-1041`. Tests use a **private named NSPasteboard**
(`NSPasteboard(name:)`) so they never touch the user's real clipboard.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/ClipboardWatcherTests.swift`:

```swift
import Testing
import Foundation
import AppKit
@testable import AnyClipDaemon
@testable import AnyClipCore

private func privatePasteboard() -> NSPasteboard {
    NSPasteboard(name: NSPasteboard.Name("anyclip-test-\(UUID().uuidString)"))
}

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-watch-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@MainActor
private func makeWatcher(
    _ pb: NSPasteboard, received: URL,
    changes: Locked<[ClipPayload]>, skipped: Locked<[String]>
) -> ClipboardWatcher {
    ClipboardWatcher(
        pasteboard: pb, pollInterval: 0.05, receivedDir: received,
        callbacks: ClipboardWatcher.Callbacks(
            onChange: { changes.set(changes.get() + [$0]) },
            onFileSkipped: { skipped.set(skipped.get() + [$0]) }))
}

@Test @MainActor func textChangeFiresOnChange() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    pb.clearContents()
    pb.setString("fresh text", forType: .string)
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .text(let s) = got[0] { #expect(s == "fresh text") } else { Issue.record("not text") }
}

@Test @MainActor func unchangedChangeCountSkipsAllReads() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    await watcher.pollOnceForTesting()
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func preexistingClipboardContentIsBaselinedNotSent() async throws {
    let pb = privatePasteboard()
    pb.clearContents()
    pb.setString("already there", forType: .string)
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func emptyTextIsNotPropagated() async throws {
    let pb = privatePasteboard()
    pb.clearContents()
    pb.setString("seed", forType: .string)
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    pb.clearContents()
    pb.setString("", forType: .string)
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func updateLocalTextDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    watcher.updateLocalText("from peer")
    #expect(pb.string(forType: .string) == "from peer")
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func imageChangeFiresOnceThenCooldownAbsorbs() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    // 1x1 red PNG
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1, pixelsHigh: 1,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.setColor(.red, atX: 0, y: 0)
    let png1 = rep.representation(using: .png, properties: [:])!
    pb.clearContents()
    pb.setData(png1, forType: .png)
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
    // Different bytes within the cooldown window: absorbed silently.
    rep.setColor(.blue, atX: 0, y: 0)
    let png2 = rep.representation(using: .png, properties: [:])!
    pb.clearContents()
    pb.setData(png2, forType: .png)
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
}

@Test @MainActor func folderOnClipboardIsSkippedWithToastOnce() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let folder = tempDir() // a real directory
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
    #expect(skipped.get().count == 1)
    #expect(skipped.get()[0].contains("folders are not supported"))
    // Same copy is never re-detected.
    await watcher.pollOnceForTesting()
    #expect(skipped.get().count == 1)
}

@Test @MainActor func smallFileOnClipboardIsSent() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let file = tempDir().appendingPathComponent("note.txt")
    try Data("file-body".utf8).write(to: file)
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, let data) = got[0] {
        #expect(name == "note.txt")
        #expect(data == Data("file-body".utf8))
    } else { Issue.record("not a file payload") }
}

@Test @MainActor func oversizedFileIsSkipped() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let file = tempDir().appendingPathComponent("big.bin")
    // budget is ~11.6 MB; 12 MiB exceeds it.
    let fh = FileManager.default
    fh.createFile(atPath: file.path, contents: nil)
    let handle = try FileHandle(forWritingTo: file)
    try handle.truncate(atOffset: UInt64(12 * 1024 * 1024))
    try handle.close()
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func updateLocalFileWritesToReceivedDirAndDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let received = tempDir()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
    let ok = watcher.updateLocalFile(name: "in:va/lid.txt", data: Data("x".utf8))
    #expect(ok)
    // basename rule: os.path.basename("in:va/lid.txt") == "lid.txt", so the
    // ":" never reaches the sanitized name.
    let target = received.appendingPathComponent("lid.txt")
    #expect(FileManager.default.fileExists(atPath: target.path))
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/ClipboardWatcher.swift`:

```swift
import Foundation
import AppKit
import AnyClipCore

/// (path, size, mtime ns) fingerprint so file bytes are only read when the
/// clipboard's file reference actually changes. Port of the Python tuple.
public struct FileFingerprint: Equatable, Sendable {
    public let path: String
    public let size: Int
    public let mtimeNs: Int64
    public let isDirectory: Bool

    public init?(url: URL) {
        var st = stat()
        guard stat(url.path, &st) == 0 else { return nil }
        path = url.path
        size = Int(st.st_size)
        mtimeNs = Int64(st.st_mtimespec.tv_sec) * 1_000_000_000
            + Int64(st.st_mtimespec.tv_nsec)
        isDirectory = (st.st_mode & S_IFMT) == S_IFDIR
    }
}

/// Polls NSPasteboard for text/image/file changes and applies inbound
/// updates without echoing them back. Port of anyclip.ClipboardWatcher.
/// changeCount gating means unchanged clipboards cost one property read
/// per poll (cheaper than the Python re-read).
@MainActor
public final class ClipboardWatcher {
    public struct Callbacks {
        public var onChange: @Sendable (ClipPayload) async -> Void
        public var onFileSkipped: (@Sendable (String) async -> Void)?
        public init(
            onChange: @escaping @Sendable (ClipPayload) async -> Void,
            onFileSkipped: (@Sendable (String) async -> Void)? = nil
        ) {
            self.onChange = onChange
            self.onFileSkipped = onFileSkipped
        }
    }

    static let imageCooldown: Double = 1.0
    /// Reserve ~256 KB for the JSON envelope and the b64 1.34x inflation.
    static let fileBudget = Int(Double(Wire.maxPayload - 256 * 1024) * 0.74)

    private let pasteboard: NSPasteboard
    private let pollInterval: Double
    private let callbacks: Callbacks
    private let receivedDir: URL

    private var lastChangeCount: Int
    private var lastText: String?
    private var lastImageHash: String?
    private var lastImageSendAt: Double = 0
    private var lastFileFingerprint: FileFingerprint?
    private var oversizeFileWarned = false

    public init(
        pasteboard: NSPasteboard = .general, pollInterval: Double,
        receivedDir: URL, callbacks: Callbacks
    ) {
        self.pasteboard = pasteboard
        self.pollInterval = pollInterval
        self.receivedDir = receivedDir
        self.callbacks = callbacks
        // Seed baselines so whatever is on the clipboard at startup never
        // fires a spurious initial send.
        lastChangeCount = pasteboard.changeCount
        lastText = pasteboard.string(forType: .string)
        if let png = Self.grabImage(pasteboard) {
            lastImageHash = sha256Hex(png)
        }
        if let url = Self.grabFileURL(pasteboard) {
            lastFileFingerprint = FileFingerprint(url: url)
        }
    }

    public func run() async throws {
        while true {
            await pollOnce()
            try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
        }
    }

    /// Test seam: one poll cycle without the sleep loop.
    public func pollOnceForTesting() async { await pollOnce() }

    private func pollOnce() async {
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        lastChangeCount = count

        // Text. Empty strings update the baseline but are NOT propagated
        // (macOS Screenshot briefly clears the clipboard mid-capture).
        let text = pasteboard.string(forType: .string)
        if let text, text != lastText {
            lastText = text
            if !text.isEmpty {
                await callbacks.onChange(.text(text))
            } else {
                AnyLog.shared.debug("clipboard cleared (empty text); not propagating")
            }
        }

        // Image. Multi-representation floods right after a screenshot are
        // collapsed by the cooldown.
        if let png = Self.grabImage(pasteboard) {
            let hash = sha256Hex(png)
            if hash != lastImageHash {
                let now = monotonicNow()
                if now - lastImageSendAt < Self.imageCooldown {
                    lastImageHash = hash
                    AnyLog.shared.debug("image change within cooldown, dropping")
                } else {
                    lastImageHash = hash
                    lastImageSendAt = now
                    await callbacks.onChange(.image(png))
                }
            }
        }

        await checkFileClipboard()
    }

    private func checkFileClipboard() async {
        guard let url = Self.grabFileURL(pasteboard) else { return }
        guard let fingerprint = FileFingerprint(url: url) else { return }
        guard fingerprint != lastFileFingerprint else { return }
        // Folders are an explicit scope-out. Record the fingerprint FIRST
        // so the same copy is never re-detected (no retry loop).
        if fingerprint.isDirectory {
            lastFileFingerprint = fingerprint
            let display = url.lastPathComponent
            AnyLog.shared.warning("folder on clipboard not synced (unsupported): \(url.path)")
            if let onSkipped = callbacks.onFileSkipped {
                await onSkipped("folder not synced — folders are not supported: \(display)")
            }
            return
        }
        if fingerprint.size > Self.fileBudget {
            if !oversizeFileWarned {
                AnyLog.shared.warning(
                    "file \(url.path) too large to sync "
                    + "(\(fingerprint.size) bytes > limit \(Self.fileBudget)); skipping")
                oversizeFileWarned = true
            }
            lastFileFingerprint = fingerprint
            return
        }
        oversizeFileWarned = false
        guard let data = try? Data(contentsOf: url) else {
            // A path that cannot be read now will not become readable by
            // polling it forever.
            lastFileFingerprint = fingerprint
            AnyLog.shared.warning("file read failed for \(url.path); skipping")
            return
        }
        lastFileFingerprint = fingerprint
        await callbacks.onChange(.file(name: url.lastPathComponent, data: data))
    }

    // ---- inbound (peer -> local clipboard) ------------------------------

    public func updateLocalText(_ text: String) {
        lastText = text
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        lastChangeCount = pasteboard.changeCount
    }

    public func updateLocalImage(_ png: Data) -> Bool {
        // Baseline BEFORE writing so a racing poll cannot echo.
        lastImageHash = sha256Hex(png)
        pasteboard.clearContents()
        let ok = pasteboard.setData(png, forType: .png)
        lastChangeCount = pasteboard.changeCount
        if !ok { AnyLog.shared.warning("clipboard write (image) failed") }
        return ok
    }

    public func updateLocalFile(name: String, data: Data) -> Bool {
        let safe = sanitizeFilename(name)
        do {
            try FileManager.default.createDirectory(
                at: receivedDir, withIntermediateDirectories: true)
            let target = receivedDir.appendingPathComponent(safe)
            try data.write(to: target)
            lastFileFingerprint = FileFingerprint(url: target)
            pasteboard.clearContents()
            let ok = pasteboard.writeObjects([target as NSURL])
            lastChangeCount = pasteboard.changeCount
            if !ok { AnyLog.shared.warning("clipboard write (file) failed") }
            return ok
        } catch {
            AnyLog.shared.warning("file write to \(receivedDir.path) failed: \(error)")
            return false
        }
    }

    // ---- pasteboard readers ---------------------------------------------

    static func grabFileURL(_ pb: NSPasteboard) -> URL? {
        let options: [NSPasteboard.ReadingOptionKey: Any] =
            [.urlReadingFileURLsOnly: true]
        let urls = pb.readObjects(forClasses: [NSURL.self], options: options) as? [URL]
        return urls?.first
    }

    /// PNG bytes of an inline image, or nil. File references take priority
    /// as their own kind (mirrors PIL ImageGrab returning a path list).
    static func grabImage(_ pb: NSPasteboard) -> Data? {
        if grabFileURL(pb) != nil { return nil }
        if let png = pb.data(forType: .png) { return png }
        if let tiff = pb.data(forType: .tiff),
           let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            return png
        }
        return nil
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Note: these tests must run in a GUI session (NSPasteboard). They use private pasteboards, so the real clipboard is untouched.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: clipboard watcher (changeCount polling, cooldown, file budget)"
```

---

### Task 14: Daemon — PermissionProbe + Watchdogs

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/PermissionProbe.swift`
- Create: `formacOS/Sources/AnyClipDaemon/Watchdogs.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/PermissionProbeTests.swift`

Reference: `permission_probe.py`, `anyclip.py:1679-1862` (watchdog loops).
The watchdog loops are thin compositions over already-tested pieces; they are
exercised by the daemon assembly test in Task 15 and the manual checklist.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/PermissionProbeTests.swift`:

```swift
import Testing
@testable import AnyClipDaemon

@Test func noNetworkWinsOverNoEvents() {
    #expect(decideProbe(eventsSeen: 0, hasNetwork: false) == .noNetwork)
}

@Test func zeroEventsWithNetworkMeansBlocked() {
    #expect(decideProbe(eventsSeen: 0, hasNetwork: true) == .blockedLocalNetwork)
}

@Test func anyEventMeansOK() {
    #expect(decideProbe(eventsSeen: 1, hasNetwork: true) == .ok)
    #expect(decideProbe(eventsSeen: 42, hasNetwork: true) == .ok)
}

@Test func probeWaitsThenJudges() async throws {
    let result = try await runProbe(
        eventsSeen: { 3 }, hasNetwork: { true }, waitSeconds: 0.01)
    #expect(result == .ok)
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/PermissionProbe.swift`:

```swift
import Foundation

/// Startup self-diagnosis for the macOS Local Network permission.
/// Port of permission_probe.py: 0 mDNS events in the observation window
/// while a network interface exists => the permission is likely revoked.
public enum ProbeResult: String, Sendable {
    case ok
    case blockedLocalNetwork = "blocked_local_network"
    case noNetwork = "no_network"
}

public func decideProbe(eventsSeen: Int, hasNetwork: Bool) -> ProbeResult {
    if !hasNetwork { return .noNetwork }
    if eventsSeen <= 0 { return .blockedLocalNetwork }
    return .ok
}

public func runProbe(
    eventsSeen: @escaping @Sendable () async -> Int,
    hasNetwork: @escaping @Sendable () -> Bool,
    waitSeconds: Double = 30.0
) async throws -> ProbeResult {
    try await Task.sleep(nanoseconds: UInt64(waitSeconds * 1_000_000_000))
    return decideProbe(eventsSeen: await eventsSeen(), hasNetwork: hasNetwork())
}
```

`formacOS/Sources/AnyClipDaemon/Watchdogs.swift`:

```swift
import Foundation
import AnyClipCore

/// Thrown by watchdogs to unwind the task group; the in-process supervisor
/// restarts the daemon with backoff (Python: RuntimeError -> supervisor).
public struct DaemonRestartError: Error, CustomStringConvertible {
    public let message: String
    public var description: String { message }
    public init(_ message: String) { self.message = message }
}

func sleepSeconds(_ seconds: Double) async throws {
    try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
}

/// App-layer ping every `interval` seconds while linked, so a silently-dead
/// TCP socket surfaces as a send failure instead of idling for hours.
public func linkPingLoop(link: PeerLink, interval: Double = 30) async throws {
    while true {
        try await sleepSeconds(interval)
        if await link.isActive { await link.sendPing() }
    }
}

/// Bounce the daemon when the host IPv4 changes — the Bonjour advertisement
/// carries the old address and quietly stops working otherwise.
public func networkWatchdog(beacon: MdnsBeacon, interval: Double = 15) async throws {
    while true {
        try await sleepSeconds(interval)
        guard let previous = await beacon.advertisedIP else { continue }
        if let current = primaryIPv4(), current != previous {
            throw DaemonRestartError(
                "local IPv4 changed: \(previous) -> \(current); "
                + "restarting daemon to re-advertise mDNS")
        }
    }
}

/// Self-heal mDNS when the link sits dead too long: refresh browse +
/// re-announce up to `refreshAttempts` times, then bounce the daemon.
public func idleLinkWatchdog(
    beacon: MdnsBeacon, link: PeerLink,
    idleThreshold: Double = 60, refreshAttempts: Int = 3
) async throws {
    var consecutiveIdle = 0
    while true {
        try await sleepSeconds(idleThreshold)
        if await link.isActive {
            consecutiveIdle = 0
            continue
        }
        consecutiveIdle += 1
        if consecutiveIdle <= refreshAttempts {
            AnyLog.shared.info(
                "link idle \(Int(idleThreshold * Double(consecutiveIdle)))s; refreshing mDNS "
                + "(attempt \(consecutiveIdle)/\(refreshAttempts))")
            await beacon.refresh()
            await link.reAnnounce()
        } else {
            throw DaemonRestartError(
                "link idle with no recovery after \(refreshAttempts) mDNS refresh "
                + "attempts; bouncing daemon")
        }
    }
}

/// Retry every known mDNS peer while unlinked. Backoff 1s -> 60s; sessions
/// that lasted > 5s reset it; 3 consecutive fast fails prune the address.
public func mdnsReconnectLoop(beacon: MdnsBeacon, link: PeerLink) async throws {
    var backoff: Double = 1
    while true {
        if await link.isActive {
            backoff = 1
            try await sleepSeconds(2)
            continue
        }
        let peers = await beacon.peersSnapshot()
        if peers.isEmpty {
            try await sleepSeconds(2)
            continue
        }
        var attempted = false
        for peer in peers {
            if await link.isActive { break }
            attempted = true
            let start = monotonicNow()
            await link.tryConnect(to: peer.endpoint, label: peer.label)
            let elapsed = monotonicNow() - start
            if await link.isActive {
                await beacon.clearFails(label: peer.label)
                if elapsed > 5 { backoff = 1 }
                break
            }
            if elapsed > 5 {
                // Handshake succeeded and the session lived a while before
                // dropping — a healthy peer, not a prune candidate.
                await beacon.clearFails(label: peer.label)
                continue
            }
            let fails = await beacon.recordFail(label: peer.label)
            if fails >= Wire.maxReconnectFails {
                await beacon.pruneAddress(label: peer.label)
                AnyLog.shared.info(
                    "pruned stale peer address \(peer.label) after \(fails) failed "
                    + "attempts; awaiting fresh mDNS discovery")
            }
        }
        if await link.isActive { continue }
        if attempted {
            try await sleepSeconds(min(backoff, 60))
            backoff = min(backoff * 2, 60)
        } else {
            try await sleepSeconds(2)
        }
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: permission probe + watchdog loops"
```

---

### Task 15: Daemon — assembly (Daemon, SyncCoordinator, supervisor)

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/Daemon.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift`

Reference: `anyclip.py:1893-2046` (run), `anyclip.py:2090-2106` (supervisor loop), `app/daemon_supervisor.py`.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/DaemonTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipDaemon
@testable import AnyClipCore

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-daemon-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func syncCoordinatorSuppressesEcho() async {
    let c = SyncCoordinator()
    await c.markReceived(kind: "text", hash: "h1")
    #expect(!(await c.shouldSend(kind: "text", hash: "h1")))
    #expect(await c.shouldSend(kind: "text", hash: "h2"))
    #expect(await c.shouldSend(kind: "image", hash: "h1"))
}

@Test func clearDirectoryFilesRemovesFilesKeepsSubdirs() throws {
    let dir = tempDir()
    try Data("x".utf8).write(to: dir.appendingPathComponent("a.txt"))
    try FileManager.default.createDirectory(
        at: dir.appendingPathComponent("sub"), withIntermediateDirectories: true)
    clearDirectoryFiles(dir)
    let remaining = try FileManager.default.contentsOfDirectory(atPath: dir.path)
    #expect(remaining == ["sub"])
}

@Test func daemonStartsAndShutsDownCleanly() async throws {
    // Full assembly on a non-default port + isolated state dir, no peers.
    // Verifies: pid file written, listener up, cancellation cleans up.
    let stateDir = tempDir()
    let config = DaemonConfig(
        token: "test-token", port: 28481, name: "daemon-test",
        pollInterval: 0.1, notify: false)
    let daemon = Daemon(
        config: config, appVersion: "0.0.0-test", stateDir: stateDir,
        notifier: { _, _ in }, onFatal: { _ in })

    let runTask = Task { await daemon.runForever() }
    // Wait for the pid file to appear.
    let pidFile = stateDir.appendingPathComponent("anyclip.pid")
    var appeared = false
    for _ in 0..<100 {
        if FileManager.default.fileExists(atPath: pidFile.path) { appeared = true; break }
        try await Task.sleep(nanoseconds: 50_000_000)
    }
    #expect(appeared)

    runTask.cancel()
    _ = await runTask.value
    // PID file released on graceful shutdown.
    #expect(!FileManager.default.fileExists(atPath: pidFile.path))
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/Daemon.swift`:

```swift
import Foundation
import AppKit
import AnyClipCore

public struct DaemonConfig: Sendable {
    public var token: String
    public var port: UInt16
    public var name: String
    public var pollInterval: Double
    public var notify: Bool

    public init(
        token: String, port: UInt16 = Wire.defaultPort,
        name: String = ProcessInfo.processInfo.hostName,
        pollInterval: Double = 0.5, notify: Bool = true
    ) {
        self.token = token
        self.port = port
        self.name = name
        self.pollInterval = max(0.1, pollInterval)
        self.notify = notify
    }
}

/// Echo-suppression state shared by the inbound and outbound paths.
public actor SyncCoordinator {
    private var suppressor = EchoSuppressor()
    public init() {}
    public func markReceived(kind: String, hash: String) {
        suppressor.markReceived(kind: kind, payloadHash: hash)
    }
    public func shouldSend(kind: String, hash: String) -> Bool {
        suppressor.shouldSend(kind: kind, payloadHash: hash)
    }
}

/// Remove regular files (not subdirectories) from a directory.
/// Port of anyclip.clear_received_dir.
public func clearDirectoryFiles(_ dir: URL) {
    let fm = FileManager.default
    guard let entries = try? fm.contentsOfDirectory(
        at: dir, includingPropertiesForKeys: [.isDirectoryKey])
    else { return }
    for entry in entries {
        let isDir = (try? entry.resourceValues(forKeys: [.isDirectoryKey]))?
            .isDirectory ?? false
        if !isDir { try? fm.removeItem(at: entry) }
    }
}

/// Assembles and supervises one daemon runtime: PeerLink + MdnsBeacon +
/// ClipboardWatcher + watchdogs, restarting with 1s -> 60s backoff on
/// errors (improvement over the Python GUI build, where watchdog-raised
/// restarts died in DaemonSupervisor).
public final class Daemon: @unchecked Sendable {
    public let events: AsyncStream<DaemonEvent>
    private let eventsCont: AsyncStream<DaemonEvent>.Continuation

    private let config: DaemonConfig
    private let appVersion: String
    private let stateDir: URL
    private let notifier: @Sendable (String, String) -> Void
    private let onFatal: @Sendable (String) -> Void

    public init(
        config: DaemonConfig, appVersion: String,
        stateDir: URL = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true),
        notifier: @escaping @Sendable (String, String) -> Void,
        onFatal: @escaping @Sendable (String) -> Void
    ) {
        self.config = config
        self.appVersion = appVersion
        self.stateDir = stateDir
        self.notifier = notifier
        self.onFatal = onFatal
        (events, eventsCont) = AsyncStream.makeStream(of: DaemonEvent.self)
    }

    public func runForever() async {
        var backoff: Double = 1
        while !Task.isCancelled {
            do {
                try await runOnce()
                return
            } catch is CancellationError {
                return
            } catch let fatal as FatalStartupError {
                AnyLog.shared.error("fatal: \(fatal.message)")
                onFatal(fatal.message)
                return
            } catch {
                if Task.isCancelled { return }
                AnyLog.shared.error("daemon crashed: \(error); restarting in \(Int(backoff))s")
                try? await sleepSeconds(backoff)
                backoff = min(backoff * 2, 60)
            }
        }
    }

    private func runOnce() async throws {
        try PidLock.prepare(port: config.port, dir: stateDir)
        let receivedDir = stateDir.appendingPathComponent("received")
        clearDirectoryFiles(receivedDir)

        let nodeID = UUID().uuidString.lowercased()
        let coordinator = SyncCoordinator()
        let emit: @Sendable (DaemonEvent) -> Void = { [eventsCont] event in
            eventsCont.yield(event)
        }
        let notify: @Sendable (String, String) -> Void =
            config.notify ? notifier : { _, _ in }

        let link = PeerLink(
            config: PeerLink.LinkConfig(
                token: config.token, port: config.port,
                name: config.name, appVersion: appVersion),
            nodeID: nodeID)

        // Holder breaks the watcher <-> link callback cycle (Python uses a
        // forward-declared closure variable for the same reason).
        let watcherBox = Locked<ClipboardWatcher?>(nil)

        // [weak link]: the closure is stored BY link itself; a strong
        // capture would leak one PeerLink per supervisor restart.
        await link.setHandlers(
            onClip: { [coordinator, weak link] payload in
                await coordinator.markReceived(kind: payload.kind, hash: payload.payloadHash)
                let peer = await link?.peerName ?? "peer"
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
                }
            },
            emit: emit)

        let onLocalChange: @Sendable (ClipPayload) async -> Void = { [coordinator] payload in
            guard await link.isActive else { return }
            guard await coordinator.shouldSend(
                kind: payload.kind, hash: payload.payloadHash)
            else {
                AnyLog.shared.debug("skip echo of just-received \(payload.kind)")
                return
            }
            await link.sendClip(payload)
            let peer = await link.peerName ?? "peer"
            switch payload {
            case .text(let text):
                AnyLog.shared.info("-> sent text \(text.count) chars to \(peer)")
                notify("AnyClip → \(peer)", preview(text))
            case .image(let png):
                AnyLog.shared.info("-> sent image \(png.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "image (\(png.count / 1024) KB)")
            case .file(let name, let data):
                AnyLog.shared.info("-> sent file \(name) \(data.count) bytes to \(peer)")
                notify("AnyClip → \(peer)", "file: \(name) (\(data.count / 1024) KB)")
            }
        }

        let watcher = await MainActor.run {
            ClipboardWatcher(
                pollInterval: config.pollInterval, receivedDir: receivedDir,
                callbacks: ClipboardWatcher.Callbacks(
                    onChange: onLocalChange,
                    onFileSkipped: { message in notify("AnyClip", message) }))
        }
        watcherBox.set(watcher)

        let beacon = MdnsBeacon(
            nodeID: nodeID, emit: emit,
            onPeer: { endpoint, label in
                await link.tryConnect(to: endpoint, label: label)
            })

        let txtData = TXTCodec.encode([
            ("id", nodeID),
            ("version", "\(Wire.legacyVersion)"),
            ("app_version", appVersion),
            ("protocol_major", "\(Wire.protocolMajor)"),
            ("protocol_minor", "\(Wire.protocolMinor)"),
        ])
        await link.configureAdvertising(
            instanceName: "\(config.name)-\(nodeID.prefix(8))", txtData: txtData)
        await beacon.start()
        AnyLog.shared.info(
            "AnyClip starting (node \(nodeID.prefix(8)), name=\(config.name))")

        do {
            try await withThrowingTaskGroup(of: Void.self) { group in
                group.addTask { try await link.serve() }
                group.addTask { @MainActor in try await watcher.run() }
                group.addTask { try await mdnsReconnectLoop(beacon: beacon, link: link) }
                group.addTask { try await networkWatchdog(beacon: beacon) }
                group.addTask { try await idleLinkWatchdog(beacon: beacon, link: link) }
                group.addTask { try await linkPingLoop(link: link) }
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
                // Wait for ALL (= asyncio.gather): the first throw cancels
                // the rest when the for-loop rethrows.
                for try await _ in group {}
            }
            await cleanup(link: link, beacon: beacon, receivedDir: receivedDir)
        } catch {
            await cleanup(link: link, beacon: beacon, receivedDir: receivedDir)
            throw error
        }
    }

    private func cleanup(link: PeerLink, beacon: MdnsBeacon, receivedDir: URL) async {
        await link.shutdown()
        await beacon.stop()
        PidLock.release(dir: stateDir)
        clearDirectoryFiles(receivedDir)
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`
Note: `daemonStartsAndShutsDownCleanly` opens a real listener on 28481 and may trigger the Local Network prompt once on macOS 15+. Approve it.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: daemon assembly + in-process supervisor"
```

---

### Task 16: Interop — Python fake peer + cross-implementation test

**Files:**
- Create: `formacOS/Scripts/fake_peer.py`
- Test: `formacOS/Tests/AnyClipDaemonTests/InteropTests.swift`

This is the spec's key acceptance gate: the Swift PeerLink must handshake
and exchange clips with the **Python encoding rules** end-to-end over TCP.

- [ ] **Step 1: Write the fake peer**

`formacOS/Scripts/fake_peer.py`:

```python
#!/usr/bin/env python3
"""Wire-compatible fake AnyClip peer for interop tests. Stdlib only.

Implements the exact frame + handshake rules of anyclip.py (PeerLink._send/
_recv/_session): 4-byte big-endian length + UTF-8 JSON; hello carries the
sha256-hex token. Listens on 127.0.0.1:<port>, accepts ONE connection,
handshakes, then:

  1. sends one text clip ("hello-from-python"),
  2. appends every received frame as a JSON line to --out,
  3. answers ping with pong,
  4. exits when the connection closes.

Prints READY on stdout once listening.
"""
import argparse
import base64
import hashlib
import json
import socket
import struct
import sys
import time
import uuid


def send_frame(conn: socket.socket, obj: dict) -> None:
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    conn.sendall(struct.pack(">I", len(data)) + data)


def recv_exactly(conn: socket.socket, n: int):
    buf = b""
    while len(buf) < n:
        chunk = conn.recv(n - len(buf))
        if not chunk:
            return None
        buf += chunk
    return buf


def recv_frame(conn: socket.socket):
    head = recv_exactly(conn, 4)
    if head is None:
        return None
    (n,) = struct.unpack(">I", head)
    if n == 0 or n > 16 * 1024 * 1024:
        return None
    body = recv_exactly(conn, n)
    if body is None:
        return None
    return json.loads(body.decode("utf-8"))


def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, required=True)
    ap.add_argument("--token", required=True)
    ap.add_argument("--out", required=True)
    args = ap.parse_args()

    token_hash = hashlib.sha256(args.token.encode("utf-8")).hexdigest()
    node_id = str(uuid.uuid4())
    out = open(args.out, "w", encoding="utf-8")

    def record(event: str, payload) -> None:
        out.write(json.dumps({"event": event, "data": payload},
                             ensure_ascii=False) + "\n")
        out.flush()

    srv = socket.create_server(("127.0.0.1", args.port))
    sys.stdout.write("READY\n")
    sys.stdout.flush()
    conn, _addr = srv.accept()

    hello = recv_frame(conn)
    record("hello", hello)
    send_frame(conn, {
        "type": "hello", "token": token_hash, "node_id": node_id,
        "name": "fake-peer", "version": 1, "app_version": "9.9.9-test",
        "protocol_major": 1, "protocol_minor": 0,
    })
    if (not hello or hello.get("type") != "hello"
            or hello.get("token") != token_hash):
        record("auth_failed", None)
        conn.close()
        return

    text = "hello-from-python"
    send_frame(conn, {
        "type": "clip", "kind": "text", "content": text,
        "hash": hashlib.sha256(text.encode("utf-8")).hexdigest(),
        "ts": time.time(),
    })

    while True:
        msg = recv_frame(conn)
        if msg is None:
            break
        if msg.get("type") == "ping":
            send_frame(conn, {"type": "pong", "ts": time.time()})
        clipped = dict(msg)
        content = clipped.get("content")
        if isinstance(content, str) and len(content) > 300:
            clipped["content"] = f"<{len(content)} chars>"
        record("recv", clipped)
    record("closed", None)


if __name__ == "__main__":
    main()
```

- [ ] **Step 2: Write the interop test**

`formacOS/Tests/AnyClipDaemonTests/InteropTests.swift`:

```swift
import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func scriptsDir() -> URL {
    // <pkg>/Tests/AnyClipDaemonTests/InteropTests.swift -> <pkg>/Scripts
    URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // AnyClipDaemonTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // formacOS
        .appendingPathComponent("Scripts")
}

@Test func interopWithPythonFakePeer() async throws {
    let port: UInt16 = 28491
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", "interop-token",
        "--out", outFile.path,
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    defer { if process.isRunning { process.terminate() } }

    // Wait for READY.
    let readyData = stdout.fileHandleForReading.availableData
    #expect(String(data: readyData, encoding: .utf8)?.contains("READY") == true)

    let clips = Locked<[ClipPayload]>([])
    let events = Locked<[DaemonEvent]>([])
    let link = PeerLink(
        config: PeerLink.LinkConfig(
            token: "interop-token", port: 28492, name: "swift-interop",
            appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(
        onClip: { clips.set(clips.get() + [$0]) },
        emit: { events.set(events.get() + [$0]) })

    let sessionTask = Task {
        await link.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
            label: "127.0.0.1:\(port)")
    }
    defer { sessionTask.cancel() }

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    // Link comes up with the Python peer's name.
    #expect(await waitUntil(5) { await link.isActive })
    #expect(await link.peerName == "fake-peer")

    // Python -> Swift text clip arrives.
    #expect(await waitUntil(5) {
        clips.get().contains {
            if case .text("hello-from-python") = $0 { return true }
            return false
        }
    })

    // Swift -> Python: text + image + file.
    await link.sendClip(.text("hello-from-swift"))
    await link.sendClip(.image(Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])))
    await link.sendClip(.file(name: "노트.txt", data: Data("file-content".utf8)))
    await link.sendPing()

    #expect(await waitUntil(5) {
        guard let lines = try? String(contentsOf: outFile, encoding: .utf8) else {
            return false
        }
        return lines.contains("hello-from-swift")
            && lines.contains("\"kind\": \"file\"")
            && lines.contains("노트.txt")
            && lines.contains("\"kind\": \"image\"")
            && lines.contains("\"type\": \"ping\"")
    })

    // The hello we sent must satisfy Python's field expectations.
    let outText = try String(contentsOf: outFile, encoding: .utf8)
    let helloLine = outText.split(separator: "\n").first { $0.contains("\"event\": \"hello\"") }
    #expect(helloLine != nil)
    #expect(helloLine!.contains("\"version\": 1"))
    #expect(helloLine!.contains("\"protocol_major\": 1"))

    await link.shutdown()
}
```

- [ ] **Step 3: Run the interop test**

Run: `swift test --package-path formacOS --filter InteropTests`
Expected: PASS. If python3 is missing, the test fails at `process.run()` — install CLT python or adjust the env path.

- [ ] **Step 4: Run the whole suite**

Run: `swift test --package-path formacOS`
Expected: all PASS.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: interop test against Python wire implementation"
```

---

### Task 17: App — Autostart + Notifier

**Files:**
- Create: `formacOS/Sources/AnyClipDaemon/AutostartPlist.swift` (in the daemon target so it is reachable from the existing test target — executables cannot be imported by tests)
- Create: `formacOS/Sources/AnyClipApp/Notifier.swift`
- Test: `formacOS/Tests/AnyClipDaemonTests/AutostartTests.swift`

Reference: `autostart.py:42-112`.

- [ ] **Step 1: Write failing tests**

`formacOS/Tests/AnyClipDaemonTests/AutostartTests.swift`:

```swift
import Testing
import Foundation
@testable import AnyClipDaemon

private func tempHome() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-home-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func enableWritesLaunchAgentPlist() throws {
    let home = tempHome()
    let auto = Autostart(homeDir: home, runLaunchctl: false)
    #expect(!auto.isEnabled())
    try auto.enable(executablePath: "/Applications/AnyClip.app/Contents/MacOS/AnyClip")
    #expect(auto.isEnabled())

    let data = try Data(contentsOf: auto.plistPath)
    let plist = try PropertyListSerialization.propertyList(
        from: data, format: nil) as! [String: Any]
    #expect(plist["Label"] as? String == "com.anyclip")
    #expect(plist["ProgramArguments"] as? [String] ==
        ["/Applications/AnyClip.app/Contents/MacOS/AnyClip"])
    #expect(plist["RunAtLoad"] as? Bool == true)
    #expect(plist["KeepAlive"] as? Bool == true)
    let stdout = plist["StandardOutPath"] as? String
    #expect(stdout?.hasSuffix(".anyclip/launchd.stdout.log") == true)
}

@Test func plistPathUsesSharedPythonLabel() {
    let auto = Autostart(homeDir: tempHome(), runLaunchctl: false)
    #expect(auto.plistPath.path.hasSuffix("Library/LaunchAgents/com.anyclip.plist"))
}

@Test func disableRemovesPlist() throws {
    let home = tempHome()
    let auto = Autostart(homeDir: home, runLaunchctl: false)
    try auto.enable(executablePath: "/x/AnyClip")
    auto.disable()
    #expect(!auto.isEnabled())
    auto.disable() // idempotent
}
```

- [ ] **Step 2: Run tests — expect compile failure**

Run: `swift test --package-path formacOS`

- [ ] **Step 3: Implement**

`formacOS/Sources/AnyClipDaemon/AutostartPlist.swift`:

```swift
import Foundation
import AnyClipCore

/// LaunchAgent registration. Deliberately uses the SAME label/path as the
/// Python app (com.anyclip) so a migrating user never ends up with two
/// autostart entries fighting over port 24816. Port of autostart.MacAutostart.
public struct Autostart {
    public static let label = "com.anyclip"

    let homeDir: URL
    let runLaunchctl: Bool

    public init(
        homeDir: URL = FileManager.default.homeDirectoryForCurrentUser,
        runLaunchctl: Bool = true
    ) {
        self.homeDir = homeDir
        self.runLaunchctl = runLaunchctl
    }

    public var plistPath: URL {
        homeDir.appendingPathComponent(
            "Library/LaunchAgents/\(Self.label).plist")
    }

    public func isEnabled() -> Bool {
        FileManager.default.fileExists(atPath: plistPath.path)
    }

    public func enable(executablePath: String) throws {
        let logDir = homeDir.appendingPathComponent(".anyclip")
        try FileManager.default.createDirectory(
            at: logDir, withIntermediateDirectories: true)
        let plist: [String: Any] = [
            "Label": Self.label,
            "ProgramArguments": [executablePath],
            "RunAtLoad": true,
            "KeepAlive": true,
            // launchd does not expand ~ -- absolute paths only.
            "StandardOutPath": logDir.appendingPathComponent("launchd.stdout.log").path,
            "StandardErrorPath": logDir.appendingPathComponent("launchd.stderr.log").path,
        ]
        try FileManager.default.createDirectory(
            at: plistPath.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try PropertyListSerialization.data(
            fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: plistPath)
        if runLaunchctl {
            // Unload first in case we are overwriting -- launchctl refuses
            // to load an already-registered label.
            launchctl(["unload", plistPath.path])
            launchctl(["load", plistPath.path])
        }
    }

    public func disable() {
        if isEnabled(), runLaunchctl {
            launchctl(["unload", plistPath.path])
        }
        try? FileManager.default.removeItem(at: plistPath)
    }

    private func launchctl(_ args: [String]) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = args
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            AnyLog.shared.warning("launchctl \(args) failed: \(error)")
        }
    }
}
```

`formacOS/Sources/AnyClipApp/Notifier.swift`:

```swift
import Foundation
import UserNotifications
import AnyClipCore
import AnyClipDaemon  // Locked

/// Desktop toasts via UserNotifications. Outside a proper .app bundle (or
/// when authorization is denied) every call is a silent no-op — calling
/// UNUserNotificationCenter without a bundle would crash.
final class Notifier: @unchecked Sendable {
    private let enabled = Locked(false)

    func setup() {
        guard Bundle.main.bundleIdentifier != nil else {
            AnyLog.shared.warning("not running from a bundle; notifications disabled")
            return
        }
        UNUserNotificationCenter.current().requestAuthorization(
            options: [.alert]) { [enabled] granted, _ in
            enabled.set(granted)
            if !granted {
                AnyLog.shared.warning("notification permission denied; toasts disabled")
            }
        }
    }

    func notify(title: String, body: String) {
        guard enabled.get() else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = String(body.prefix(240))
        let request = UNNotificationRequest(
            identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}
```

- [ ] **Step 4: Run tests — expect pass**

Run: `swift test --package-path formacOS`

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: launch-agent autostart + notifier"
```

---

### Task 18: App — Onboarding, StatusItemController, AppDelegate, main

**Files:**
- Create: `formacOS/Sources/AnyClipApp/Onboarding.swift`
- Create: `formacOS/Sources/AnyClipApp/StatusItemController.swift`
- Create: `formacOS/Sources/AnyClipApp/AppDelegate.swift`
- Modify: `formacOS/Sources/AnyClipApp/main.swift` (replace placeholder)
- Delete: `formacOS/Sources/AnyClipCore/Placeholder.swift` (no longer referenced)
- Modify: `formacOS/Tests/AnyClipCoreTests/SmokeTests.swift` — replace the marker assertion with `#expect(Wire.protocolMajor == 1)`

Reference: `app/onboarding.py`, `app/menubar_mac.py`. AppKit UI — verified by
build + the Task 19 manual checklist (no UI unit tests).

- [ ] **Step 1: Implement Onboarding**

`formacOS/Sources/AnyClipApp/Onboarding.swift`:

```swift
import AppKit
import AnyClipCore

/// First-launch token dialog. Port of app/onboarding.py + the resolution
/// order from menubar_mac._resolve_token: env > on-disk config > dialog.
@MainActor
enum Onboarding {
    static func resolveToken() -> String? {
        if let env = ProcessInfo.processInfo.environment["ANYCLIP_TOKEN"],
           !env.isEmpty {
            return env
        }
        if let stored = ConfigStore.load() {
            return stored.token
        }
        guard let token = show() else { return nil }
        try? ConfigStore.save(StoredConfig(token: token))
        return token
    }

    private static func show() -> String? {
        let alert = NSAlert()
        alert.messageText = "Welcome to AnyClip"
        alert.informativeText =
            "Choose how to set the shared clipboard token. Both devices "
            + "must use the same value."
        alert.addButton(withTitle: "Generate new token (first device)")
        alert.addButton(withTitle: "Enter existing token (second device)")
        alert.addButton(withTitle: "Cancel")
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            return ConfigStore.generateToken()
        case .alertSecondButtonReturn:
            return promptForToken()
        default:
            return nil
        }
    }

    private static func promptForToken() -> String? {
        let alert = NSAlert()
        alert.messageText = "Enter shared token"
        alert.informativeText = "Paste the token shown on your first device."
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        let value = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
}
```

- [ ] **Step 2: Implement StatusItemController**

`formacOS/Sources/AnyClipApp/StatusItemController.swift`:

```swift
import AppKit
import AnyClipCore
import AnyClipDaemon

/// Menu bar shell. Static "@" title; all state lives in the dropdown.
/// Port of app/menubar_mac.AnyClipMenubarApp (minus Sparkle).
@MainActor
final class StatusItemController: NSObject {
    private static let lanSettingsURL =
        "x-apple.systempreferences:com.apple.preference.security"
        + "?Privacy_LocalNetwork"

    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private let statusMenuItem = NSMenuItem(
        title: "Status: Idle", action: nil, keyEquivalent: "")
    private let lastSyncItem = NSMenuItem(
        title: "Last sync: —", action: nil, keyEquivalent: "")
    private var lanSettingsItem: NSMenuItem?
    private let startAtLoginItem: NSMenuItem
    private let autostart = Autostart()
    private let logFileURL: URL
    private let onQuit: () -> Void

    init(logFileURL: URL, onQuit: @escaping () -> Void) {
        self.logFileURL = logFileURL
        self.onQuit = onQuit
        statusItem = NSStatusBar.system.statusItem(
            withLength: NSStatusItem.variableLength)
        startAtLoginItem = NSMenuItem(
            title: "Start at Login", action: nil, keyEquivalent: "")
        super.init()

        statusItem.button?.title = "@"

        statusMenuItem.isEnabled = false
        lastSyncItem.isEnabled = false
        menu.autoenablesItems = false

        let tokenItem = NSMenuItem(
            title: "Token…", action: #selector(showTokenInfo), keyEquivalent: "")
        tokenItem.target = self
        startAtLoginItem.action = #selector(toggleAutostart)
        startAtLoginItem.target = self
        startAtLoginItem.state = autostart.isEnabled() ? .on : .off
        let openLogsItem = NSMenuItem(
            title: "Open Logs", action: #selector(openLogs), keyEquivalent: "")
        openLogsItem.target = self
        let quitItem = NSMenuItem(
            title: "Quit", action: #selector(quit), keyEquivalent: "")
        quitItem.target = self

        menu.addItem(statusMenuItem)
        menu.addItem(lastSyncItem)
        menu.addItem(.separator())
        menu.addItem(tokenItem)
        menu.addItem(startAtLoginItem)
        menu.addItem(openLogsItem)
        menu.addItem(.separator())
        menu.addItem(quitItem)
        statusItem.menu = menu
    }

    func apply(_ state: PeerUIState) {
        switch state.kind {
        case .linked:
            statusMenuItem.title = "Linked: \(state.peerName ?? "peer")"
            let formatter = DateFormatter()
            formatter.dateFormat = "HH:mm:ss"
            lastSyncItem.title = "Linked since: \(formatter.string(from: Date()))"
            removeLanSettingsItem()
        case .searching:
            statusMenuItem.title = "Searching for peer"
            removeLanSettingsItem()
        case .error:
            let reason = state.reason ?? "unknown"
            statusMenuItem.title = "Error: \(reason)"
            if reason == "local_network" {
                addLanSettingsItem()
            } else {
                removeLanSettingsItem()
            }
        case .idle:
            statusMenuItem.title = "Idle"
            removeLanSettingsItem()
        }
    }

    // ---- menu actions ---------------------------------------------------

    @objc private func showTokenInfo() {
        let stored = ConfigStore.load()
        let current = stored?.token ?? "(no token configured)"
        let path = ConfigStore.configPath().path

        let alert = NSAlert()
        alert.messageText = "AnyClip token"
        alert.informativeText =
            "Current token (select to copy):\n\(current)\n\n"
            + "Stored at: \(path)\n\n"
            + "Reset generates a new random token, saves it, and quits "
            + "AnyClip. The other device must re-onboard with the new "
            + "token before sync resumes."
        alert.addButton(withTitle: "Close")
        alert.addButton(withTitle: "Reset…")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        let confirm = NSAlert()
        confirm.messageText = "Reset token?"
        confirm.informativeText =
            "This will replace the current token with a fresh one. Your "
            + "other device will stop syncing until you paste the new "
            + "token there."
        confirm.addButton(withTitle: "Reset")
        confirm.addButton(withTitle: "Cancel")
        guard confirm.runModal() == .alertFirstButtonReturn else { return }

        let newToken = ConfigStore.generateToken()
        try? ConfigStore.save(StoredConfig(token: newToken))
        let done = NSAlert()
        done.messageText = "Token reset"
        done.informativeText =
            "New token saved:\n\(newToken)\n\n"
            + "AnyClip will now quit. Relaunch to apply, then paste this "
            + "token on your other device."
        done.runModal()
        quit()
    }

    @objc private func toggleAutostart() {
        if startAtLoginItem.state == .on {
            autostart.disable()
            startAtLoginItem.state = .off
        } else {
            let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
            try? autostart.enable(executablePath: exe)
            startAtLoginItem.state = .on
        }
    }

    @objc private func openLogs() {
        NSWorkspace.shared.activateFileViewerSelecting([logFileURL])
    }

    @objc private func openLanSettings() {
        if let url = URL(string: Self.lanSettingsURL) {
            NSWorkspace.shared.open(url)
        }
    }

    private func addLanSettingsItem() {
        guard lanSettingsItem == nil else { return }
        let item = NSMenuItem(
            title: "Open Local Network Settings",
            action: #selector(openLanSettings), keyEquivalent: "")
        item.target = self
        // Insert just above "Open Logs", like the Python menu.
        let index = menu.items.firstIndex { $0.title == "Open Logs" } ?? menu.items.count
        menu.insertItem(item, at: index)
        lanSettingsItem = item
    }

    private func removeLanSettingsItem() {
        guard let item = lanSettingsItem else { return }
        menu.removeItem(item)
        lanSettingsItem = nil
    }

    @objc private func quit() {
        onQuit()
    }
}
```

- [ ] **Step 3: Implement AppDelegate + main**

`formacOS/Sources/AnyClipApp/AppDelegate.swift`:

```swift
import AppKit
import AnyClipCore
import AnyClipDaemon

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: StatusItemController?
    private var daemon: Daemon?
    private var daemonTask: Task<Void, Never>?
    private let notifier = Notifier()

    func applicationDidFinishLaunching(_ notification: Notification) {
        let stateDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true)
        let logURL = stateDir.appendingPathComponent("anyclip.log")
        AnyLog.shared.configure(fileURL: logURL, verbose: false)

        guard let token = Onboarding.resolveToken() else {
            FileHandle.standardError.write(
                Data("anyclip: onboarding cancelled, exiting\n".utf8))
            NSApp.terminate(nil)
            return
        }
        notifier.setup()

        let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"]
            as? String)
            ?? ProcessInfo.processInfo.environment["ANYCLIP_BUILD_VERSION"]
            ?? "0.0.0-dev"

        let config = DaemonConfig(token: token)
        let daemon = Daemon(
            config: config, appVersion: appVersion,
            notifier: { [notifier] title, body in
                notifier.notify(title: title, body: body)
            },
            onFatal: { message in
                Task { @MainActor in
                    let alert = NSAlert()
                    alert.messageText = "AnyClip cannot start"
                    alert.informativeText = message
                    alert.runModal()
                    NSApp.terminate(nil)
                }
            })
        self.daemon = daemon

        controller = StatusItemController(logFileURL: logURL) { [weak self] in
            self?.quitGracefully()
        }

        daemonTask = Task { await daemon.runForever() }

        // Fold daemon events into UI state on the main actor.
        Task { @MainActor [weak self] in
            guard let events = self?.daemon?.events else { return }
            var state = PeerUIState.initial
            self?.controller?.apply(state)
            for await event in events {
                state = reducePeerState(
                    state, event, now: Date().timeIntervalSince1970)
                self?.controller?.apply(state)
            }
        }
    }

    private func quitGracefully() {
        let task = daemonTask
        task?.cancel()
        Task {
            // Give cleanup (mDNS unregister, pid release) up to 3 s,
            // matching the Python supervisor.stop(timeout=3).
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await task?.value }
                group.addTask {
                    try? await Task.sleep(nanoseconds: 3_000_000_000)
                }
                await group.next()
                group.cancelAll()
            }
            await MainActor.run { NSApp.terminate(nil) }
        }
    }
}
```

Replace `formacOS/Sources/AnyClipApp/main.swift` with:

```swift
import AppKit

let app = NSApplication.shared
app.setActivationPolicy(.accessory) // menu bar only; also LSUIElement in Info.plist
let delegate = AppDelegate()
app.delegate = delegate
app.run()
```

- [ ] **Step 4: Build + run tests**

Run: `swift build --package-path formacOS && swift test --package-path formacOS`
Expected: builds, all tests still PASS.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: AppKit shell (onboarding, menu bar, app delegate)"
```

---

### Task 19: Bundle — Info.plist template + build script + manual smoke

**Files:**
- Create: `formacOS/Resources/Info.plist.template`
- Create: `formacOS/Scripts/build-app.sh` (chmod +x)

- [ ] **Step 1: Create the Info.plist template**

`formacOS/Resources/Info.plist.template`:

```xml
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN"
 "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleDevelopmentRegion</key>
    <string>en</string>
    <key>CFBundleExecutable</key>
    <string>AnyClip</string>
    <key>CFBundleIdentifier</key>
    <string>com.anyclip.AnyClip</string>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>CFBundleName</key>
    <string>AnyClip</string>
    <key>CFBundleDisplayName</key>
    <string>AnyClip</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleShortVersionString</key>
    <string>__VERSION__</string>
    <key>CFBundleVersion</key>
    <string>__VERSION__</string>
    <key>CFBundleIconFile</key>
    <string>anyclip.icns</string>
    <key>LSMinimumSystemVersion</key>
    <string>14.0</string>
    <key>LSUIElement</key>
    <true/>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>NSLocalNetworkUsageDescription</key>
    <string>AnyClip discovers your other device on the local network to sync the clipboard.</string>
    <key>NSBonjourServices</key>
    <array>
        <string>_anyclip._tcp</string>
    </array>
</dict>
</plist>
```

- [ ] **Step 2: Create the build script**

`formacOS/Scripts/build-app.sh`:

```bash
#!/bin/bash
# Assemble AnyClip.app from the SwiftPM release build.
# Usage: Scripts/build-app.sh   (run from anywhere; cd's to the package)
# Version: env ANYCLIP_BUILD_VERSION (default 0.0.0-dev), mirroring the
# Python CI convention.
set -euo pipefail
cd "$(dirname "$0")/.."

VERSION="${ANYCLIP_BUILD_VERSION:-0.0.0-dev}"
APP="dist/AnyClip.app"

swift build -c release --arch arm64

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp .build/arm64-apple-macosx/release/AnyClipApp "$APP/Contents/MacOS/AnyClip"
sed "s/__VERSION__/$VERSION/g" Resources/Info.plist.template \
    > "$APP/Contents/Info.plist"
printf 'APPL????' > "$APP/Contents/PkgInfo"
cp ../app/icons/anyclip.icns "$APP/Contents/Resources/anyclip.icns"

# Ad-hoc signature: keeps the existing right-click-to-open Gatekeeper flow.
codesign --force --sign - "$APP"

echo "Built $APP (version $VERSION)"
```

Run: `chmod +x formacOS/Scripts/build-app.sh`

- [ ] **Step 3: Build the bundle**

Run: `formacOS/Scripts/build-app.sh`
Expected: `Built dist/AnyClip.app (version 0.0.0-dev)`; `codesign --verify formacOS/dist/AnyClip.app` exits 0.

- [ ] **Step 4: Manual smoke checklist**

> ⚠️ The packaged app shares `~/.anyclip/` and port 24816 with any running
> Python AnyClip, and its PID lock will TERMINATE a running Python instance.
> Check with the user before launching if a daemon may be running.

Run: `open formacOS/dist/AnyClip.app` and verify:

1. First launch without `~/.anyclip/config.json`: onboarding alert appears; "Generate new token" creates `~/.anyclip/config.json` (0600).
2. "@" appears in the menu bar; dropdown shows `Status: Idle` → (within seconds) `Searching for peer` once mDNS discovery emits, or stays Idle when no peers exist.
3. macOS may prompt for Local Network — approve; with the permission denied, after ~30 s the menu shows `Error: local_network` + "Open Local Network Settings".
4. `Open Logs` reveals `~/.anyclip/anyclip.log` in Finder; the log contains `listening on tcp/24816` and `AnyClip starting`.
5. `Start at Login` toggle writes/removes `~/Library/LaunchAgents/com.anyclip.plist`.
6. `Quit` removes `~/.anyclip/anyclip.pid` and the process exits.
7. If a second device with the Python build is available: copy text both ways, copy an image, copy a small file — all three sync; menu shows `Linked: <peer>`.

Record the results (pass/fail per item) in the final report.

- [ ] **Step 5: Commit**

```bash
git add formacOS
git commit -m "formacOS: app bundle template + build script"
```

---

### Task 20: Final verification + formacOS README

**Files:**
- Create: `formacOS/README.md`

- [ ] **Step 1: Full test suite + clean build**

Run: `swift test --package-path formacOS && formacOS/Scripts/build-app.sh`
Expected: all tests PASS; bundle builds.

- [ ] **Step 2: Run the Python test suite (regression guard)**

Run: `python3 -m pytest tests/ -q` (from the repo root, with the project venv if present: `.venv/bin/python -m pytest tests/ -q`)
Expected: same results as before this work — the port must not have touched Python code.

- [ ] **Step 3: Write formacOS/README.md**

```markdown
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

## Not ported (deliberate)

Sparkle auto-update, `--headless` CLI mode, multi-file/folder sync.
See `../docs/superpowers/specs/2026-06-11-macos-native-port-design.md`.
```

- [ ] **Step 4: Commit**

```bash
git add formacOS/README.md
git commit -m "formacOS: build/usage README"
```

- [ ] **Step 5: Final report**

Summarize for the user: test counts, bundle path, manual checklist results,
and the two known caveats (ad-hoc signing ⇒ right-click-to-open once;
Python's case-sensitive PID check won't kill a running Swift instance).

---

## Self-Review Notes

- **Spec coverage:** wire protocol (T6/T7/T11/T16), mDNS (T11 advertise,
  T12 browse), clipboard semantics (T13), state machine + menu bar (T3/T18),
  onboarding + config (T5/T18), process hygiene (T8 logging, T9 pid lock,
  T15 received/ cleanup), watchdogs + probe (T14), autostart (T17),
  build/bundle (T19), tests incl. golden vectors + Python interop (T7/T16).
  Toast notifications: injected callback (T15) + UNUserNotificationCenter
  (T17), with the spec's exact title/body strings in T15.
- **Known API risk points** (flagged inline): NWTXTRecord subscript/Sequence
  (T12 has a fallback), `NWListener.Service(txtRecord: Data)` overload (T11),
  swift-testing availability (T1 gates on it). If any fails to compile, the
  executor adapts locally and notes it in the task report.
- **Port-number hygiene:** every test uses a unique 284xx port to avoid
  collisions inside one `swift test` run (28461-28463, 28471-28476, 28481,
  28491-28492); the daemon default 24816 is only used by the packaged app.




