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

@Test func staleCountDoesNotCarryIntoNewWindow() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<4 { gate.recordFail("10.0.0.1") } // 4 fails, then quiet
    clock.t += 61                                   // cooldown fully elapsed
    gate.recordFail("10.0.0.1")                     // first fail of new window
    #expect(!gate.isBlocked("10.0.0.1"))            // must NOT be blocked (count restarted at 1)
}

@Test func sweepEvictsStaleOtherIPs() {
    let clock = FakeClock()
    var gate = AuthGate(now: { clock.now() })
    for _ in 0..<5 { gate.recordFail("10.0.0.1") }
    clock.t += 61
    gate.recordFail("10.0.0.2")                     // triggers sweep of stale 10.0.0.1
    for _ in 0..<4 { gate.recordFail("10.0.0.1") }  // 4 fresh fails — old 5 must be gone
    #expect(!gate.isBlocked("10.0.0.1"))
}
