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
    for i in 1...(handshakeFailThreshold - 1) {
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
    #expect(s.kind == .linked)
    #expect(s.consecutiveHandshakeFails == 0)
}
