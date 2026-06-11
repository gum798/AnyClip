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
