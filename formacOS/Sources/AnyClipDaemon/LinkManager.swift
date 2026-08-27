import Foundation
import Network
import AnyClipCore

/// Sentinel thrown internally when bind fails with EADDRINUSE; triggers a retry.
private struct PortInUseError: Error {}

/// Outcome of an outbound dial; consumed by mdnsReconnectLoop's fail bookkeeping.
public enum ConnectOutcome: Sendable {
    case routed   // handshake succeeded (link created, replaced, or tie-broken)
    case failed   // handshake failed (auth/version/timeout/connect)
    case atCap    // skipped: already at the peer cap
    case busy     // skipped: a dial to this address is already in flight
}

/// Per-copy broadcast result: which peers got the (possibly downgraded) clip,
/// the largest old-peer file-drop count for the aggregated fallback toast, and
/// the peers the legacy size gate skipped for the aggregated size toast.
public struct BroadcastResult: Sendable {
    public var delivered: [(peerName: String, payload: ClipPayload)]
    public var maxDropped: Int
    /// Peers whose 16 MiB receive cap this clip would have breached (protocol
    /// < 1.2). Their links stay UP; the caller emits ONE toast per clip.
    public var sizeSkipped: [String]
    public init(
        delivered: [(peerName: String, payload: ClipPayload)], maxDropped: Int,
        sizeSkipped: [String] = []
    ) {
        self.delivered = delivered
        self.maxDropped = maxDropped
        self.sizeSkipped = sizeSkipped
    }
}

/// Owns the listening socket, the active-link table (keyed by peer node_id), the
/// pre-routing gate, and the broadcast fan-out. Full-mesh replacement for the
/// single-link PeerLink server half. Port of anyclip.LinkManager — actor
/// isolation replaces the asyncio Lock; the routing/registration block runs with
/// NO awaits, so it is atomic exactly like the Python critical section.
public actor LinkManager {
    public static let defaultMaxPeers = 8

    public struct LinkConfig: Sendable {
        public var token: String
        public var port: UInt16
        public var name: String
        public var appVersion: String
        public init(token: String, port: UInt16, name: String, appVersion: String) {
            self.token = token
            self.port = port
            self.name = name
            self.appVersion = appVersion
        }
    }

    private struct LinkEntry {
        let link: PeerLink
        let task: Task<Void, Never>
        let linkedAt: Double
        let gen: Int
    }

    private let config: LinkConfig
    private let nodeID: String
    private let tokenHash: String
    private let maxPeers: Int
    private let pingInterval: Double
    private let pingDeadFactor: Double
    private var authGate: AuthGate

    private var onClip: (@Sendable (ClipPayload, String) async -> Void)?
    private var emit: (@Sendable (DaemonEvent) -> Void)?

    private var links: [String: LinkEntry] = [:]
    private var connecting: Set<String> = []
    private var linkGen = 0

    private var listener: NWListener?
    public private(set) var isServing = false
    private var advertiseService: NWListener.Service?

    public init(
        config: LinkConfig, nodeID: String,
        maxPeers: Int = defaultMaxPeers,
        pingInterval: Double = 30, pingDeadFactor: Double = 3
    ) {
        self.config = config
        self.nodeID = nodeID
        self.tokenHash = sha256Hex(config.token)
        self.maxPeers = maxPeers
        self.pingInterval = pingInterval
        self.pingDeadFactor = pingDeadFactor
        self.authGate = AuthGate()
    }

    public func setHandlers(
        onClip: @escaping @Sendable (ClipPayload, String) async -> Void,
        emit: @escaping @Sendable (DaemonEvent) -> Void
    ) {
        self.onClip = onClip
        self.emit = emit
    }

    // ---- link-table queries --------------------------------------------
    public func activeLinkCount() -> Int { links.count }
    public func hasLink(nodeID: String) -> Bool { links[nodeID] != nil }
    public var atCap: Bool { links.count >= maxPeers }

    // ---- advertising (Bonjour lives on the listener) -------------------
    public func configureAdvertising(instanceName: String, txtData: Data) {
        advertiseService = NWListener.Service(
            name: instanceName, type: Wire.serviceType, domain: nil, txtRecord: txtData)
    }

    public func reAnnounce() {
        guard let listener, let advertiseService else { return }
        listener.service = nil
        listener.service = advertiseService
        AnyLog.shared.debug("mDNS: re-announced service")
    }

    // ---- serve (moved from PeerLink) -----------------------------------
    public func serve() async throws {
        guard self.listener == nil else {
            throw FatalStartupError("serve() called twice on the same LinkManager")
        }
        var attempt = 0
        let listener = try await makeAndStartListener(attempt: &attempt)
        listener.stateUpdateHandler = nil
        isServing = true
        AnyLog.shared.info("listening on tcp/\(config.port)")
        defer {
            listener.cancel()
            self.listener = nil
            isServing = false
        }
        while true { try await Task.sleep(nanoseconds: 1_000_000_000) }
    }

    private func makeAndStartListener(attempt: inout Int) async throws -> NWListener {
        while true {
            let tcp = NWProtocolTCP.Options()
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 15
            tcp.keepaliveCount = 4
            tcp.keepaliveInterval = 5
            let params = NWParameters(tls: nil, tcp: tcp)
            params.allowLocalEndpointReuse = true
            let candidate: NWListener
            do {
                candidate = try NWListener(
                    using: params, on: NWEndpoint.Port(rawValue: config.port)!)
            } catch {
                throw FatalStartupError("could not open tcp/\(config.port): \(error)")
            }
            candidate.service = advertiseService
            candidate.newConnectionHandler = { [weak self] conn in
                guard let self else { conn.cancel(); return }
                Task { await self.handleInbound(conn) }
            }
            self.listener = candidate
            do {
                try await withCheckedThrowingContinuation {
                    (cont: CheckedContinuation<Void, Error>) in
                    let resumed = Locked(false)
                    candidate.stateUpdateHandler = { state in
                        switch state {
                        case .ready:
                            if !resumed.exchange(true) { cont.resume() }
                        case .failed(let error):
                            if !resumed.exchange(true) {
                                if case .posix(let code) = error, code == .EADDRINUSE {
                                    cont.resume(throwing: PortInUseError())
                                } else {
                                    cont.resume(throwing: error)
                                }
                            }
                        case .cancelled:
                            if !resumed.exchange(true) {
                                cont.resume(throwing: WireConnectionError.cancelled)
                            }
                        default:
                            break
                        }
                    }
                    candidate.start(queue: .global(qos: .userInitiated))
                }
                return candidate
            } catch is PortInUseError {
                candidate.cancel()
                self.listener = nil
                attempt += 1
                guard attempt <= 4 else {
                    throw FatalStartupError(
                        "port \(config.port) still in use after cleanup attempt; "
                        + "another process may have grabbed it")
                }
                AnyLog.shared.info(
                    "tcp/\(config.port) still in use; retrying bind (\(attempt)/4)")
                try await Task.sleep(nanoseconds: 500_000_000)
            }
        }
    }

    private func handleInbound(_ conn: NWConnection) async {
        let framed = FramedConnection(connection: conn)
        do { try await framed.start() } catch { framed.cancel(); return }
        AnyLog.shared.debug("inbound from \(framed.remoteIP ?? "?")")
        if let ip = framed.remoteIP, authGate.isBlocked(ip) {
            AnyLog.shared.info(
                "auth gate: \(ip) blocked (>\(AuthGate.maxFails) failures, "
                + "cooldown \(Int(AuthGate.cooldown))s)")
            framed.cancel()
            return
        }
        _ = await handshakeAndRoute(framed, inbound: true)
        // On success the routed PeerLink owns `framed`; every refusal path in
        // handshakeAndRoute already cancelled it. Do NOT cancel here.
    }

    // ---- outbound dial -------------------------------------------------
    public func tryConnect(to endpoint: NWEndpoint, label: String) async -> ConnectOutcome {
        if connecting.contains(label) {
            AnyLog.shared.debug("connect to \(label) already in flight, skipping")
            return .busy
        }
        if links.count >= maxPeers { return .atCap }
        connecting.insert(label)
        defer { connecting.remove(label) }
        let framed = FramedConnection.outbound(to: endpoint)
        do {
            try await withTimeout(seconds: Wire.connectTimeout) { try await framed.start() }
        } catch {
            AnyLog.shared.info("connect to \(label) failed: \(error)")
            framed.cancel()
            return .failed
        }
        AnyLog.shared.debug("outbound connected to \(label)")
        let routed = await handshakeAndRoute(framed, inbound: false)
        return routed ? .routed : .failed
    }

    // ---- gate + routing ------------------------------------------------
    /// Exchange hellos, run the full pre-routing gate (IP block/record, token,
    /// version negotiation w/ major refusal, self/loopback drop), then route.
    /// Returns true when the handshake succeeds (link created/replaced or
    /// tie-broken); false on any refusal (framed cancelled on every false path).
    private func handshakeAndRoute(_ framed: FramedConnection, inbound: Bool) async -> Bool {
        let myHello = WireMessage.hello(
            tokenHash: tokenHash, nodeID: nodeID,
            name: config.name, appVersion: config.appVersion)
        do { try await framed.sendFrame(myHello) } catch { framed.cancel(); return false }
        let addr = framed.remoteIP ?? ""

        let peerHello: WireMessage?
        do {
            peerHello = try await withTimeout(seconds: Wire.handshakeTimeout) {
                try await framed.receiveMessage()
            }
        } catch is TimeoutError {
            AnyLog.shared.warning("handshake timeout")
            emit?(.handshakeFailed(addr: addr, reason: "timeout"))
            framed.cancel(); return false
        } catch {
            framed.cancel(); return false
        }
        guard let hello = peerHello, hello.type == "hello" else {
            AnyLog.shared.warning("invalid hello, closing")
            emit?(.handshakeFailed(addr: addr, reason: "invalid"))
            framed.cancel(); return false
        }
        let peerIP = inbound ? framed.remoteIP : nil
        guard hello.token == tokenHash else {
            AnyLog.shared.warning("auth failed from peer name=\(hello.name ?? "?")")
            if let ip = peerIP { authGate.recordFail(ip) }
            emit?(.handshakeFailed(addr: peerIP ?? addr, reason: "auth"))
            framed.cancel(); return false
        }
        let peerVersion = hello.peerVersionInfo()
        let localVersion = VersionInfo(
            appVersion: config.appVersion,
            protocolMajor: Wire.protocolMajor, protocolMinor: Wire.protocolMinor)
        let compat = negotiate(local: localVersion, peer: peerVersion)
        guard linkAllowed(compat) else {
            AnyLog.shared.warning(
                "version refused: local proto=\(Wire.protocolMajor).\(Wire.protocolMinor) "
                + "vs peer proto=\(peerVersion.protocolMajor).\(peerVersion.protocolMinor) "
                + "app=\(peerVersion.appVersion) -> \(compat.rawValue)")
            emit?(.handshakeFailed(addr: addr, reason: "version:\(compat.rawValue)"))
            framed.cancel(); return false
        }
        if compat != .compatible {
            AnyLog.shared.info("version mismatch (link kept): \(compat.rawValue)")
        }
        guard let peerID = hello.node_id, peerID != nodeID else {
            AnyLog.shared.debug("self loopback or bad node_id, dropping")
            framed.cancel(); return false
        }
        if let ip = peerIP { authGate.recordOK(ip) }
        let display = (hello.name?.isEmpty == false) ? hello.name! : String(peerID.prefix(8))

        route(framed: framed, peerID: peerID, name: display,
              peerMinor: peerVersion.protocolMinor, inbound: inbound,
              appVersion: peerVersion.appVersion)
        return true
    }

    /// Registration / tie-break / cap. Synchronous & await-free = atomic.
    private func route(
        framed: FramedConnection, peerID: String, name: String,
        peerMinor: Int, inbound: Bool, appVersion: String
    ) {
        if let existing = links[peerID] {
            let race = (monotonicNow() - existing.linkedAt) < Wire.raceWindow
            if race {
                // Genuine simultaneous-connect: lexicographic node_id tie-break.
                let keepThisLink = (!inbound && nodeID < peerID) || (inbound && nodeID > peerID)
                if !keepThisLink {
                    AnyLog.shared.debug("tie-breaker: dropping duplicate link (race)")
                    framed.cancel()
                    return
                }
                AnyLog.shared.debug("tie-breaker: replacing existing link (race)")
            } else {
                // Established link: a newcomer means the peer thinks ours is dead.
                AnyLog.shared.info("replacing link with \(name) (peer reconnected)")
            }
            // Overwrite (below) BEFORE the old task's linkClosed can run (we are
            // in a no-await critical section), so the old link's teardown sees a
            // mismatched gen and does not remove/emit for the replacement.
            existing.task.cancel()
            existing.link.close()
            registerLink(framed: framed, peerID: peerID, name: name,
                         peerMinor: peerMinor, inbound: inbound, appVersion: appVersion)
            return
        }
        // New node_id: cap applies here only (a known reconnect is routed above).
        if links.count >= maxPeers {
            AnyLog.shared.info("peer cap reached (\(maxPeers)); refusing \(name)")
            framed.cancel()
            return
        }
        registerLink(framed: framed, peerID: peerID, name: name,
                     peerMinor: peerMinor, inbound: inbound, appVersion: appVersion)
    }

    private func registerLink(
        framed: FramedConnection, peerID: String, name: String,
        peerMinor: Int, inbound: Bool, appVersion: String
    ) {
        linkGen += 1
        let gen = linkGen
        let deliver = onClip ?? { _, _ in }
        let interval = pingInterval
        let deadFactor = pingDeadFactor
        let link = PeerLink(
            conn: framed, peerNodeID: peerID, peerName: name,
            peerProtocolMinor: peerMinor, onClip: deliver)
        // Per-link tasks: the receive loop + this link's OWN staleness dropper.
        // One sleeping peer drops only its link (spec: per-link watchdog).
        let task = Task { [weak self] in
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await link.run() }
                group.addTask {
                    try? await linkPingLoop(link: link, interval: interval, deadFactor: deadFactor)
                }
                _ = await group.next()   // run() returned (EOF / close)
                group.cancelAll()
            }
            await self?.linkClosed(peerID: peerID, gen: gen, reason: "peer disconnected")
        }
        links[peerID] = LinkEntry(link: link, task: task, linkedAt: monotonicNow(), gen: gen)
        AnyLog.shared.info(
            "linked with peer name=\(name) id=\(peerID.prefix(8)) "
            + "(\(inbound ? "inbound" : "outbound")) peer_app_version=\(appVersion) "
            + "peer_proto=\(Wire.protocolMajor).\(peerMinor) [links=\(links.count)]")
        emit?(.linkUp(nodeID: peerID, peerName: name))
    }

    /// Dead link -> remove from the table immediately (frees a cap slot) and
    /// emit linkDown. Guarded by gen so a replaced link's late teardown no-ops.
    private func linkClosed(peerID: String, gen: Int, reason: String) {
        guard let entry = links[peerID], entry.gen == gen else { return }
        links.removeValue(forKey: peerID)
        AnyLog.shared.info("peer disconnected: \(peerID.prefix(8)) [links=\(links.count)]")
        emit?(.linkDown(nodeID: peerID, reason: reason))
    }

    // ---- broadcast -----------------------------------------------------
    /// Fan a local clip out to every active link. Per-link protocol-minor
    /// downgrade is evaluated per link; a per-link send failure drops ONLY that
    /// link. Echo-suppression (shouldSend) is the caller's job — evaluated once.
    ///
    /// Two per-link gates run here, both keyed on the peer's advertised minor:
    ///  - minor < 1: a kind:"files" clip degrades to its first file (downgradeForPeer).
    ///  - minor < 2: a frame over the legacy 16 MiB receive cap is SKIPPED (the
    ///    peer would close the session on it). The link stays up and the peer
    ///    name lands in `sizeSkipped` for one aggregated toast.
    ///  - minor 1-2: folder entries are sent AS IS (the peer ignores "path" and
    ///    writes the files flat); logged once per clip per affected link.
    ///  - minor 0 + folder-only clip: nothing to send on that link (log only).
    ///
    /// Each distinct payload variant is encoded at most ONCE per broadcast (and
    /// shares one timestamp): the same bytes back the size gate and every send
    /// of that variant, so an 8-peer mesh never re-encodes the same clip.
    public func broadcast(_ payload: ClipPayload) async -> BroadcastResult {
        var delivered: [(peerName: String, payload: ClipPayload)] = []
        var sizeSkipped: [String] = []
        var maxDropped = 0
        let ts = Date().timeIntervalSince1970
        // Variant kind ("text"/"image"/"file"/"files") -> its encoded frame, or
        // nil when the payload does not fit even the 64 MiB cap.
        var frames: [String: EncodedFrame?] = [:]
        // Evaluated ONCE per clip: does this payload carry folder entries?
        var hasFolders = false
        if case .files(let fs) = payload { hasFolders = fs.contains { $0.relPath != nil } }

        for entry in links.values {
            let link = entry.link
            let (maybe, dropped) = downgradeForPeer(payload, peerMinor: link.peerProtocolMinor)
            guard let outPayload = maybe else {
                if hasFolders {
                    // Reached only when the clip is folder-ONLY: a mixed clip
                    // still has a loose entry for the fallback. Wording pinned
                    // in lockstep with anyclip's fan-out.
                    AnyLog.shared.info(
                        "folder-only clip not sent to '\(link.peerName)' "
                        + "(peer protocol 1.0)")
                }
                continue
            }
            if hasFolders, (1...2).contains(link.peerProtocolMinor) {
                AnyLog.shared.info(
                    "peer \(link.peerName) will flatten folders (protocol < 1.3)")
            }
            let variant = outPayload.kind
            let encoded: EncodedFrame?
            if let cached = frames[variant] {
                encoded = cached
            } else {
                encoded = try? WireMessage.clip(outPayload, ts: ts).encode()
                if encoded == nil {
                    AnyLog.shared.warning(
                        "payload too large (> \(Wire.maxPayload) bytes), dropping")
                }
                frames[variant] = encoded
            }
            guard let frame = encoded else { continue }
            guard Wire.linkAcceptsFrame(
                bytes: frame.bodyCount, peerMinor: link.peerProtocolMinor)
            else {
                AnyLog.shared.info(
                    "clip too large for '\(link.peerName)' (peer protocol < 1.2); skipping")
                sizeSkipped.append(link.peerName)
                continue
            }
            let ok = await link.sendEncoded(frame)
            if !ok {
                AnyLog.shared.info("send failed to \(link.peerName); dropping link")
                link.close()   // wakes run(); its task removes the entry + emits linkDown
                continue
            }
            // Only a DELIVERED downgrade counts toward the fallback toast: a
            // gated or failed link received nothing to leave files behind on.
            maxDropped = max(maxDropped, dropped)
            delivered.append((peerName: link.peerName, payload: outPayload))
        }
        return BroadcastResult(
            delivered: delivered, maxDropped: maxDropped, sizeSkipped: sizeSkipped)
    }

    // ---- shutdown ------------------------------------------------------
    public func shutdown() {
        for entry in links.values {
            entry.task.cancel()
            entry.link.close()
        }
        links.removeAll()
        listener?.cancel()
        listener = nil
        isServing = false
    }
}
