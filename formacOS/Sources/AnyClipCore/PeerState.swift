/// Daemon-event types and pure state-machine reducer for the UI shell.
/// Port of peer_state.py. Multi-peer: LinkUp/LinkDown carry a stable node_id,
/// and the UI state holds a peer collection keyed by node_id (full mesh).

public enum DaemonEvent: Sendable, Equatable {
    case peerDiscovered(name: String, addr: String)
    case linkUp(nodeID: String, peerName: String)
    case linkDown(nodeID: String, reason: String)
    case handshakeFailed(addr: String, reason: String)
    case permissionMissing(kind: String)
}

public struct PeerUIState: Sendable, Equatable {
    public enum Kind: String, Sendable { case idle, searching, linked, error }

    public var kind: Kind
    /// node_id -> display name for every currently-linked peer. Source of truth
    /// for the linked/searching split: linked iff this is non-empty.
    public var peers: [String: String]
    public var since: Double?
    public var reason: String?
    /// Internal bookkeeping so the reducer can trip into error("auth") after a
    /// run of failed handshakes while NO peer is linked. UI reads
    /// kind/peers/since/reason.
    public var consecutiveHandshakeFails: Int

    public init(
        kind: Kind,
        peers: [String: String] = [:],
        since: Double? = nil,
        reason: String? = nil,
        consecutiveHandshakeFails: Int = 0
    ) {
        self.kind = kind
        self.peers = peers
        self.since = since
        self.reason = reason
        self.consecutiveHandshakeFails = consecutiveHandshakeFails
    }

    /// Linked peer display names, ordinally sorted. The status line renders
    /// "Linked: " + these joined by ", ". Empty when not linked.
    public var sortedPeerNames: [String] { peers.values.sorted() }

    /// Back-compat single-name accessor (first sorted peer). Prefer
    /// sortedPeerNames for multi-peer callers.
    public var peerName: String? { sortedPeerNames.first }

    public static let initial = PeerUIState(kind: .idle)
}

public let handshakeFailThreshold = 5

public func reducePeerState(
    _ prev: PeerUIState, _ event: DaemonEvent, now: Double
) -> PeerUIState {
    switch event {
    case .permissionMissing(let kind):
        return PeerUIState(kind: .error, reason: kind)
    case .linkUp(let nodeID, let peerName):
        var next = prev
        next.peers[nodeID] = peerName
        next.kind = .linked
        // "Linked since" tracks the first peer of the current linked run.
        next.since = prev.peers.isEmpty ? now : prev.since
        next.reason = nil
        next.consecutiveHandshakeFails = 0
        return next
    case .linkDown(let nodeID, let reason):
        var next = prev
        next.peers[nodeID] = nil
        if next.peers.isEmpty {
            // Last peer gone -> back to the unchanged "searching" presentation.
            return PeerUIState(
                kind: .searching, reason: reason,
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        next.kind = .linked   // other peers remain; stay linked, keep `since`
        return next
    case .peerDiscovered:
        if prev.kind == .idle || prev.kind == .error {
            return PeerUIState(kind: .searching)
        }
        return prev
    case .handshakeFailed:
        var next = prev
        next.consecutiveHandshakeFails += 1
        // An established link masks the auth escalation: one stranger failing
        // auth must not flip a working multi-peer UI into error.
        if next.consecutiveHandshakeFails >= handshakeFailThreshold && next.peers.isEmpty {
            return PeerUIState(
                kind: .error, reason: "auth",
                consecutiveHandshakeFails: next.consecutiveHandshakeFails)
        }
        return next
    }
}
