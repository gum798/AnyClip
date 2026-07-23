import Testing
@testable import AnyClipCore

@Test func linkedIsPlainGlyph() {
    let s = reducePeerState(.initial, .linkUp(nodeID: "x", peerName: "p"), now: 1)
    #expect(menuIconSpec(for: s) == MenuIconSpec(text: "@", highlighted: false))
}

@Test func searchingIsHighlighted() {
    let s = reducePeerState(.initial, .peerDiscovered(name: "n", addr: "a"), now: 1)
    #expect(menuIconSpec(for: s) == MenuIconSpec(text: "@", highlighted: true))
}

@Test func idleIsHighlighted() {
    #expect(menuIconSpec(for: .initial) == MenuIconSpec(text: "@", highlighted: true))
}

@Test func errorAddsBang() {
    let s = reducePeerState(.initial, .permissionMissing(kind: "local_network"), now: 1)
    #expect(menuIconSpec(for: s) == MenuIconSpec(text: "@!", highlighted: true))
}
