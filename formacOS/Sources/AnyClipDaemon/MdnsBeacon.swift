import Foundation
import Network
import AnyClipCore

/// Browses for AnyClip peers on the LAN and keeps the reconnect bookkeeping
/// (knownPeers / addressFails / eventsSeen). Advertising itself is carried
/// by PeerLink's NWListener (configureAdvertising). Port of
/// anyclip.MdnsBeacon minus the zeroconf advertise half.
public actor MdnsBeacon {
    private let nodeID: String
    private let emit: @Sendable (DaemonEvent) -> Void
    private let onPeer: @Sendable (NWEndpoint, String) async -> Void

    private var browser: NWBrowser?
    /// peer node id -> (endpoint, label). A restarting peer mints a new
    /// node_id but keeps its address, so prune works on labels.
    private var knownPeers: [String: (endpoint: NWEndpoint, label: String)] = [:]
    private var addressFails: [String: Int] = [:]
    /// Non-self resolutions; consumed by the permission probe.
    public private(set) var eventsSeen = 0
    public private(set) var advertisedIP: String?

    public init(
        nodeID: String,
        emit: @escaping @Sendable (DaemonEvent) -> Void,
        onPeer: @escaping @Sendable (NWEndpoint, String) async -> Void
    ) {
        self.nodeID = nodeID
        self.emit = emit
        self.onPeer = onPeer
    }

    public func start() {
        advertisedIP = primaryIPv4()
        startBrowser()
    }

    private func startBrowser() {
        let browser = NWBrowser(
            for: .bonjourWithTXTRecord(type: Wire.serviceType, domain: nil),
            using: NWParameters())
        browser.browseResultsChangedHandler = { [weak self] results, _ in
            guard let self else { return }
            Task { await self.handleResults(results) }
        }
        browser.start(queue: .global(qos: .utility))
        self.browser = browser
    }

    private func handleResults(_ results: Set<NWBrowser.Result>) async {
        for result in results {
            guard case .bonjour(let txtRecord) = result.metadata else { continue }
            // NWTXTRecord does not expose a Sequence conformance in Swift 5 mode;
            // iterate only the well-known keys as a safe fallback.
            var txt: [String: String] = [:]
            for key in ["id", "version", "app_version", "protocol_major", "protocol_minor"] {
                if let value = txtRecord[key] { txt[key] = value }
            }
            let label = endpointLabel(result.endpoint)
            await ingest(txt: txt, endpoint: result.endpoint, label: label)
        }
    }

    /// Pure-ish ingestion of one resolved advertisement; exposed for tests.
    public func ingest(txt: [String: String], endpoint: NWEndpoint, label: String) async {
        guard let peerID = txt["id"] else { return }
        // Self-loopback discovery does not prove the network is alive.
        guard peerID != nodeID else { return }
        eventsSeen += 1
        knownPeers[peerID] = (endpoint, label)
        addressFails[label] = nil
        AnyLog.shared.info("discovered peer \(label)")
        emit(.peerDiscovered(name: label, addr: label))
        await onPeer(endpoint, label)
    }

    private func endpointLabel(_ endpoint: NWEndpoint) -> String {
        if case .service(let name, _, _, _) = endpoint { return name }
        return "\(endpoint)"
    }

    /// Re-issue the browse query (mDNS self-heal). The matching service
    /// re-announce lives on PeerLink.reAnnounce().
    public func refresh() {
        browser?.cancel()
        startBrowser()
        AnyLog.shared.debug("mDNS: browser re-issued")
    }

    public func stop() {
        browser?.cancel()
        browser = nil
    }

    // ---- reconnect-loop bookkeeping ------------------------------------

    /// Known peers deduped by address label (a restarted remote daemon
    /// leaves several stale node ids behind for the same address).
    public func peersSnapshot() -> [(endpoint: NWEndpoint, label: String)] {
        var seen = Set<String>()
        var out: [(endpoint: NWEndpoint, label: String)] = []
        for (_, value) in knownPeers where !seen.contains(value.label) {
            seen.insert(value.label)
            out.append(value)
        }
        return out
    }

    /// Returns the new consecutive-failure count for this address.
    public func recordFail(label: String) -> Int {
        let fails = (addressFails[label] ?? 0) + 1
        addressFails[label] = fails
        return fails
    }

    public func clearFails(label: String) {
        addressFails[label] = nil
    }

    public func pruneAddress(label: String) {
        knownPeers = knownPeers.filter { $0.value.label != label }
        addressFails[label] = nil
    }
}
