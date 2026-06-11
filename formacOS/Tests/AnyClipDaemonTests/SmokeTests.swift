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
