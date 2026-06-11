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
