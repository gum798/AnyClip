import Testing
import Foundation
@testable import AnyClipCore

/// Protocol 1.2 constants: the 64 MiB frame cap, the legacy 16 MiB receive cap
/// old peers still enforce, the size-scaled send budget, and the boundaries of
/// both guards. Mirrors the constant/timeout half of tests/test_large_frames.py.

// ---- constants ----------------------------------------------------------

@Test func frameCapsAndProtocolMinor() {
    #expect(Wire.maxPayload == 64 * 1024 * 1024)
    #expect(Wire.maxPayload == 67_108_864)
    #expect(Wire.legacyMaxPayload == 16 * 1024 * 1024)
    #expect(Wire.legacyMaxPayload == 16_777_216)
    #expect(Wire.protocolMinor == 3)
}

// ---- send timeout scales with payload -----------------------------------

@Test func sendTimeoutScalesAtOneMiBPerSecond() {
    #expect(Wire.sendTimeoutFor(bytes: 0) == Wire.sendTimeout)
    #expect(Wire.sendTimeoutFor(bytes: 1024 * 1024) == Wire.sendTimeout + 1.0)
    // Worst case must stay under the 90 s per-link staleness deadline
    // (30 s ping x dead factor 3).
    #expect(Wire.sendTimeoutFor(bytes: Wire.maxPayload) == 74.0)
    #expect(Wire.sendTimeoutFor(bytes: Wire.maxPayload) < 30.0 * 3.0)
}

@Test func sendTimeoutHonoursACustomBase() {
    // Tests (and only tests) shrink the base; the scaling still applies.
    #expect(Wire.sendTimeoutFor(bytes: 2 * 1024 * 1024, base: 0.5) == 2.5)
}

// ---- receive guard boundary ---------------------------------------------

@Test func recvGuardAcceptsUpToTheNewCap() {
    #expect(Wire.acceptsFrameLength(1))
    #expect(Wire.acceptsFrameLength(Wire.legacyMaxPayload + 1))   // above the OLD cap
    #expect(Wire.acceptsFrameLength(Wire.maxPayload))             // exactly at the cap
    #expect(!Wire.acceptsFrameLength(Wire.maxPayload + 1))
    #expect(!Wire.acceptsFrameLength(0))
    #expect(!Wire.acceptsFrameLength(-1))
}

// ---- per-link legacy gate predicate -------------------------------------

@Test func linkAcceptsFrameGatesOnlyOverCapFramesForOldPeers() {
    // At or below the legacy cap every peer takes the frame.
    for minor in 0...2 {
        #expect(Wire.linkAcceptsFrame(bytes: Wire.legacyMaxPayload, peerMinor: minor))
    }
    // One byte over: only a protocol >= 1.2 peer may receive it.
    #expect(!Wire.linkAcceptsFrame(bytes: Wire.legacyMaxPayload + 1, peerMinor: 0))
    #expect(!Wire.linkAcceptsFrame(bytes: Wire.legacyMaxPayload + 1, peerMinor: 1))
    #expect(Wire.linkAcceptsFrame(bytes: Wire.legacyMaxPayload + 1, peerMinor: 2))
    #expect(Wire.linkAcceptsFrame(bytes: Wire.maxPayload, peerMinor: 3))
}

// ---- encode guard --------------------------------------------------------

@Test func encodeAcceptsABodyBetweenTheLegacyAndNewCaps() throws {
    var msg = WireMessage(type: "clip")
    msg.kind = "text"
    // Body lands just over the legacy cap but far under the new one.
    msg.content = String(repeating: "x", count: Wire.legacyMaxPayload + 1024)
    let frame = try msg.encode()
    #expect(frame.bodyCount > Wire.legacyMaxPayload)
    #expect(frame.bodyCount <= Wire.maxPayload)
    // bodyCount is the BODY length; the frame carries the 4-byte prefix too.
    #expect(frame.bytes.count == frame.bodyCount + 4)
    #expect(WireMessage.frameLength(frame.bytes.prefix(4)) == frame.bodyCount)
}

// The over-cap side of the encode guard is `oversizedPayloadThrows` in
// WireProtocolTests, which already asserts against Wire.maxPayload; it is not
// repeated here so the suite allocates 64 MiB only once.
