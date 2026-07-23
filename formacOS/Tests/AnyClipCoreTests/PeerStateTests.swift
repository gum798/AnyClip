import Testing
@testable import AnyClipCore

@Test func initialIsIdleWithNoPeers() {
    #expect(PeerUIState.initial.kind == .idle)
    #expect(PeerUIState.initial.peers.isEmpty)
    #expect(PeerUIState.initial.sortedPeerNames.isEmpty)
}

@Test func linkUpAddsPeerAndGoesLinked() {
    let s = reducePeerState(.initial, .linkUp(nodeID: "abc", peerName: "win-pc"), now: 42.0)
    #expect(s.kind == .linked)
    #expect(s.peers == ["abc": "win-pc"])
    #expect(s.since == 42.0)
    #expect(s.consecutiveHandshakeFails == 0)
    #expect(s.peerName == "win-pc")            // back-compat accessor
}

@Test func twoPeersRenderSortedNames() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "n2", peerName: "win-pc"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "n1", peerName: "android-9"), now: 2)
    #expect(s.kind == .linked)
    #expect(s.peers.count == 2)
    #expect(s.sortedPeerNames == ["android-9", "win-pc"])  // ordinal sort by name
    #expect(s.since == 1)                                  // "since first peer" preserved
}

@Test func linkDownRemovesOnlyThatPeer() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "a", peerName: "p-a"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "b", peerName: "p-b"), now: 2)
    s = reducePeerState(s, .linkDown(nodeID: "a", reason: "peer disconnected"), now: 3)
    #expect(s.kind == .linked)                 // b still linked
    #expect(s.peers == ["b": "p-b"])
}

@Test func linkDownOfLastPeerGoesSearching() {
    let linked = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    let s = reducePeerState(linked, .linkDown(nodeID: "x", reason: "peer disconnected"), now: 2)
    #expect(s.kind == .searching)
    #expect(s.reason == "peer disconnected")
    #expect(s.peers.isEmpty)
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
    let linked = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    let s = reducePeerState(linked, .peerDiscovered(name: "n", addr: "a"), now: 2)
    #expect(s == linked)
}

@Test func permissionMissingIsError() {
    let s = reducePeerState(.initial, .permissionMissing(kind: "local_network"), now: 1)
    #expect(s.kind == .error)
    #expect(s.reason == "local_network")
}

@Test func fiveHandshakeFailsTripAuthErrorWhenNoPeers() {
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

@Test func handshakeFailsDoNotTripErrorWhileAPeerIsLinked() {
    var s = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    for i in 1...(handshakeFailThreshold + 2) {
        s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: Double(i))
    }
    #expect(s.kind == .linked)                 // an existing link masks the auth escalation
    #expect(s.peers == ["x": "p"])
}

@Test func linkUpResetsFailCounter() {
    var s = PeerUIState.initial
    s = reducePeerState(s, .handshakeFailed(addr: "a", reason: "auth"), now: 1)
    s = reducePeerState(s, .linkUp(nodeID: "x", peerName: "p"), now: 2)
    #expect(s.kind == .linked)
    #expect(s.consecutiveHandshakeFails == 0)
}
