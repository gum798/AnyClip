/// Daemon-event types and pure state-machine reducer for the UI shell.
/// Port of peer_state.py.

public enum DaemonEvent: Sendable, Equatable {
    case peerDiscovered(name: String, addr: String)
    case linkUp(peerName: String, peerID: String)
    case linkDown(reason: String)
    case handshakeFailed(addr: String, reason: String)
    case permissionMissing(kind: String)
}

public struct PeerUIState: Sendable, Equatable {
    public enum Kind: String, Sendable { case idle, searching, linked, error }

    public var kind: Kind
    public var peerName: String?
    public var since: Double?
    public var reason: String?
    /// Internal bookkeeping so the reducer can trip into error("auth")
    /// after a run of failed handshakes. UI reads kind/peerName/since/reason.
    public var consecutiveHandshakeFails: Int

    public init(
        kind: Kind,
        peerName: String? = nil,
        since: Double? = nil,
        reason: String? = nil,
        consecutiveHandshakeFails: Int = 0
    ) {
        self.kind = kind
        self.peerName = peerName
        self.since = since
        self.reason = reason
        self.consecutiveHandshakeFails = consecutiveHandshakeFails
    }

    public static let initial = PeerUIState(kind: .idle)
}

public let handshakeFailThreshold = 5

public func reducePeerState(
    _ prev: PeerUIState, _ event: DaemonEvent, now: Double
) -> PeerUIState {
    switch event {
    case .permissionMissing(let kind):
        return PeerUIState(kind: .error, reason: kind)
    case .linkUp(let peerName, _):
        return PeerUIState(kind: .linked, peerName: peerName, since: now)
    case .linkDown(let reason):
        return PeerUIState(kind: .searching, reason: reason)
    case .peerDiscovered:
        if prev.kind == .idle || prev.kind == .error {
            return PeerUIState(kind: .searching)
        }
        return prev
    case .handshakeFailed:
        var next = prev
        next.consecutiveHandshakeFails += 1
        if next.consecutiveHandshakeFails >= handshakeFailThreshold {
            return PeerUIState(
                kind: .error, reason: "auth",
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        return next
    }
}
