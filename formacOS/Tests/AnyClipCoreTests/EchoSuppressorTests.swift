import Testing
@testable import AnyClipCore

private let window = EchoSuppressor.suppressWindowSeconds

@Test func sendsWhenNothingReceived() {
    let s = EchoSuppressor()
    #expect(s.shouldSend(kind: "text", payloadHash: "h1", now: 0))
}

@Test func suppressesEchoWithinWindow() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1", now: 0)
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1", now: 0))
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1", now: window))
    #expect(s.shouldSend(kind: "text", payloadHash: "h2", now: 0))
}

@Test func deliberateRecopySendsAfterWindow() {
    // The 2026-09-02 password bug: the exact string last received from the
    // peer could never be re-sent, however much later the user re-copied it.
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1", now: 0)
    #expect(s.shouldSend(kind: "text", payloadHash: "h1", now: window + 0.001))
    #expect(s.shouldSend(kind: "text", payloadHash: "h1", now: 87))
}

@Test func remarkRearmsWindow() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1", now: 0)
    s.markReceived(kind: "text", payloadHash: "h1", now: 40)
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1", now: 60))
    #expect(s.shouldSend(kind: "text", payloadHash: "h1", now: 40 + window + 0.001))
}

@Test func kindsAreTrackedIndependently() {
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1", now: 0)
    #expect(s.shouldSend(kind: "image", payloadHash: "h1", now: 0))
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1", now: 0))
}

@Test func defaultClockSuppressesFreshReceive() {
    // No explicit now: the real monotonic clock applies; a receive marked an
    // instant ago must still be suppressed.
    var s = EchoSuppressor()
    s.markReceived(kind: "text", payloadHash: "h1")
    #expect(!s.shouldSend(kind: "text", payloadHash: "h1"))
}
