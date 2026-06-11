/// Pure version negotiation between two AnyClip peers.
/// Port of version_negotiator.py — keep the table in lockstep.

public enum Compatibility: String, Sendable, Equatable {
    case compatible = "compatible"
    case peerOlderMinor = "peer_older_minor"
    case peerNewerMinor = "peer_newer_minor"
    case peerOlderMajor = "peer_older_major"
    case peerNewerMajor = "peer_newer_major"
}

public struct VersionInfo: Sendable, Equatable {
    public let appVersion: String
    public let protocolMajor: Int
    public let protocolMinor: Int

    public init(appVersion: String, protocolMajor: Int, protocolMinor: Int) {
        self.appVersion = appVersion
        self.protocolMajor = protocolMajor
        self.protocolMinor = protocolMinor
    }
}

/// Major version dominates: any major mismatch is a refusal regardless of
/// minor. Minor differences are advisory and keep the link. appVersion is
/// informational and never affects the outcome.
public func negotiate(local: VersionInfo, peer: VersionInfo) -> Compatibility {
    if peer.protocolMajor < local.protocolMajor { return .peerOlderMajor }
    if peer.protocolMajor > local.protocolMajor { return .peerNewerMajor }
    if peer.protocolMinor < local.protocolMinor { return .peerOlderMinor }
    if peer.protocolMinor > local.protocolMinor { return .peerNewerMinor }
    return .compatible
}

public func linkAllowed(_ result: Compatibility) -> Bool {
    switch result {
    case .compatible, .peerOlderMinor, .peerNewerMinor: return true
    case .peerOlderMajor, .peerNewerMajor: return false
    }
}
