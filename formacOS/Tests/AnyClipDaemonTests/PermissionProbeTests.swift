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
