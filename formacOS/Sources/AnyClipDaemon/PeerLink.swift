import Foundation
import Network
import AnyClipCore

/// Sentinel thrown internally when bind fails with EADDRINUSE; triggers a retry.
private struct PortInUseError: Error {}

/// Owns the single active TCP link to a peer. Acts as both server and
/// client; resolves the simultaneous-connect race via lexicographic
/// node_id tie-break. Port of anyclip.PeerLink — actor isolation replaces
/// the asyncio Lock; there are no awaits inside the registration block, so
/// it is atomic exactly like the Python critical section.
public actor PeerLink {
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

    private let config: LinkConfig
    private let nodeID: String
    private let tokenHash: String
    private var authGate: AuthGate
    private var onClip: (@Sendable (ClipPayload) async -> Void)?
    private var emit: (@Sendable (DaemonEvent) -> Void)?

    private var activeConn: FramedConnection?
    private var peerNodeID: String?
    public private(set) var peerName: String?
    /// Peer's advertised protocol minor from the hello; gates the outbound
    /// files/kind:"file" downgrade. 0 when unlinked.
    public private(set) var peerProtocolMinor: Int = 0
    private var linkedAt: Double = 0
    /// Monotonic timestamp of the last inbound frame on the active link. Drives
    /// half-open detection: a peer that slept or vanished without RST/FIN keeps
    /// the socket "writable" (our pings never error) yet sends nothing back, so
    /// staleness can only be judged from inbound silence, not send failures.
    private var lastInboundAt: Double = 0
    private var connecting: Set<String> = []

    private var listener: NWListener?
    public private(set) var isServing = false
    private var advertiseService: NWListener.Service?

    public init(config: LinkConfig, nodeID: String) {
        self.config = config
        self.nodeID = nodeID
        self.tokenHash = sha256Hex(config.token)
        self.authGate = AuthGate()
    }

    public var isActive: Bool { activeConn != nil }

    public func setHandlers(
        onClip: @escaping @Sendable (ClipPayload) async -> Void,
        emit: @escaping @Sendable (DaemonEvent) -> Void
    ) {
        self.onClip = onClip
        self.emit = emit
    }

    /// Bonjour advertisement carried by the TCP listener. Must be called
    /// before serve(). instanceName is "{name}-{nodeId8}".
    public func configureAdvertising(instanceName: String, txtData: Data) {
        advertiseService = NWListener.Service(
            name: instanceName, type: Wire.serviceType, domain: nil, txtRecord: txtData)
    }

    /// Re-publish the Bonjour advertisement (mDNS self-heal).
    public func reAnnounce() {
        guard let listener, let advertiseService else { return }
        listener.service = nil
        listener.service = advertiseService
        AnyLog.shared.debug("mDNS: re-announced service")
    }

    public func serve() async throws {
        guard self.listener == nil else {
            throw FatalStartupError("serve() called twice on the same PeerLink")
        }

        // Attempt to bind up to 4 times, waiting 0.5s between attempts, to
        // survive transient EADDRINUSE after killing the previous daemon.
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
        // Park until cancelled; inbound sessions run in their own Tasks.
        while true {
            try await Task.sleep(nanoseconds: 1_000_000_000)
        }
    }

    /// Create an NWListener on `config.port`, await `.ready`, and return it.
    /// On EADDRINUSE, cancels the failed listener and throws `PortInUseError`.
    /// The caller owns the retry loop.
    private func makeAndStartListener(attempt: inout Int) async throws -> NWListener {
        while true {
            let tcp = NWProtocolTCP.Options()
            tcp.enableKeepalive = true
            tcp.keepaliveIdle = 15
            // Backstop to the app-layer heartbeat: actually probe a silent peer
            // (~15s idle + 4×5s unanswered ≈ 35s) instead of idling forever.
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

            // Capture self weakly to avoid actor-isolation issues inside the
            // NWListener closure (which runs on an arbitrary queue, not the actor).
            // Deviation from spec template: using 'nonisolated(unsafe)' is not
            // available in Swift 5 mode, so we capture a sendable reference to
            // self (PeerLink is an actor, satisfying Sendable) and dispatch a Task.
            candidate.newConnectionHandler = { [weak self] conn in
                guard let self else { conn.cancel(); return }
                Task { await self.handleInbound(conn) }
            }
            self.listener = candidate

            // Deviation: `if case .posix(.EADDRINUSE) = error` doesn't compile for
            // NWError in Swift 5 mode (NWError is not a Swift enum with associated
            // values that support direct pattern matching). Use a `where` guard instead.
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
                // Bind succeeded — return the ready listener.
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
        do {
            try await framed.start()
        } catch {
            framed.cancel()
            return
        }
        AnyLog.shared.debug("inbound from \(framed.remoteIP ?? "?")")
        if let ip = framed.remoteIP, authGate.isBlocked(ip) {
            AnyLog.shared.info(
                "auth gate: \(ip) blocked (>\(AuthGate.maxFails) failures, "
                + "cooldown \(Int(AuthGate.cooldown))s)")
            framed.cancel()
            return
        }
        await session(framed, inbound: true)
        framed.cancel()
    }

    public func tryConnect(to endpoint: NWEndpoint, label: String) async {
        if isActive { return }
        if connecting.contains(label) {
            AnyLog.shared.debug("connect to \(label) already in flight, skipping")
            return
        }
        connecting.insert(label)
        defer { connecting.remove(label) }
        let framed = FramedConnection.outbound(to: endpoint)
        do {
            try await withTimeout(seconds: Wire.connectTimeout) {
                try await framed.start()
            }
        } catch {
            AnyLog.shared.info("connect to \(label) failed: \(error)")
            framed.cancel()
            return
        }
        AnyLog.shared.debug("outbound connected to \(label)")
        await session(framed, inbound: false)
        framed.cancel()
    }

    private func session(_ framed: FramedConnection, inbound: Bool) async {
        let myHello = WireMessage.hello(
            tokenHash: tokenHash, nodeID: nodeID,
            name: config.name, appVersion: config.appVersion)
        do {
            try await framed.sendFrame(myHello)
        } catch {
            return
        }
        let addr = framed.remoteIP ?? ""

        let peerHello: WireMessage?
        do {
            peerHello = try await withTimeout(seconds: Wire.handshakeTimeout) {
                try await framed.receiveMessage()
            }
        } catch is TimeoutError {
            AnyLog.shared.warning("handshake timeout")
            emit?(.handshakeFailed(addr: addr, reason: "timeout"))
            framed.cancel()
            return
        } catch {
            return
        }
        guard let hello = peerHello, hello.type == "hello" else {
            AnyLog.shared.warning("invalid hello, closing")
            emit?(.handshakeFailed(addr: addr, reason: "invalid"))
            return
        }
        let peerIP = inbound ? framed.remoteIP : nil
        guard hello.token == tokenHash else {
            AnyLog.shared.warning("auth failed from peer name=\(hello.name ?? "?")")
            if let ip = peerIP { authGate.recordFail(ip) }
            emit?(.handshakeFailed(addr: peerIP ?? addr, reason: "auth"))
            return
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
            return
        }
        if compat != .compatible {
            AnyLog.shared.info("version mismatch (link kept): \(compat.rawValue)")
        }
        guard let peerID = hello.node_id, peerID != nodeID else {
            AnyLog.shared.debug("self loopback or bad node_id, dropping")
            return
        }
        if let ip = peerIP { authGate.recordOK(ip) }

        // Registration / tie-break. No awaits in this block — atomic.
        if activeConn != nil {
            let race = (monotonicNow() - linkedAt) < Wire.raceWindow
            if race {
                let keepThisLink =
                    (!inbound && nodeID < peerID) || (inbound && nodeID > peerID)
                if !keepThisLink {
                    AnyLog.shared.debug("tie-breaker: dropping duplicate link (race)")
                    return
                }
                AnyLog.shared.debug("tie-breaker: replacing existing link (race)")
            } else {
                AnyLog.shared.info(
                    "tie-breaker: stale link to \(peerName ?? "?") replaced by "
                    + "fresh handshake from \(hello.name ?? "?")")
            }
            activeConn?.cancel()
        }
        activeConn = framed
        peerNodeID = peerID
        peerProtocolMinor = peerVersion.protocolMinor
        let displayName = (hello.name?.isEmpty == false) ? hello.name! : String(peerID.prefix(8))
        peerName = displayName
        linkedAt = monotonicNow()
        lastInboundAt = monotonicNow()
        AnyLog.shared.info(
            "linked with peer name=\(displayName) id=\(peerID.prefix(8)) "
            + "(\(inbound ? "inbound" : "outbound")) "
            + "peer_app_version=\(peerVersion.appVersion) "
            + "peer_proto=\(peerVersion.protocolMajor).\(peerVersion.protocolMinor)")
        emit?(.linkUp(peerName: displayName, peerID: peerID))

        // Receive loop.
        while true {
            let msg: WireMessage?
            do {
                msg = try await framed.receiveMessage()
            } catch {
                break
            }
            // Any inbound frame (clip, ping, pong) proves the peer is alive;
            // refresh the liveness clock for the heartbeat deadline.
            if activeConn === framed { lastInboundAt = monotonicNow() }
            guard let m = msg else { break }
            switch m.type {
            case "clip":
                await handleClip(m)
            case "ping":
                do {
                    try await framed.sendFrame(.pong(ts: Date().timeIntervalSince1970))
                } catch {
                    AnyLog.shared.info("send failed (link likely down): \(error)")
                }
            case "pong":
                break // presence is enough
            default:
                AnyLog.shared.debug("ignoring message type: \(m.type)")
            }
        }

        let wasActive = (activeConn === framed)
        if wasActive {
            activeConn = nil
            peerNodeID = nil
            peerName = nil
            peerProtocolMinor = 0
        }
        AnyLog.shared.info("peer disconnected")
        if wasActive {
            emit?(.linkDown(reason: "peer disconnected"))
        }
    }

    private func handleClip(_ m: WireMessage) async {
        let kind = m.kind ?? "text"
        switch kind {
        case "text":
            if let content = m.content {
                await onClip?(.text(content))
            }
        case "image":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad image payload from peer")
                return
            }
            await onClip?(.image(data))
        case "file":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad file payload from peer")
                return
            }
            let name = (m.name?.isEmpty == false) ? m.name! : "received.bin"
            await onClip?(.file(name: name, data: data))
        case "files":
            guard let entries = decodeFileEntries(m.files) else {
                AnyLog.shared.warning(
                    "bad files payload from peer (empty or invalid base64); dropping frame")
                return
            }
            await onClip?(.files(entries))
        default:
            AnyLog.shared.debug("ignoring clip with kind=\(kind)")
        }
    }

    /// App-layer keepalive frame; drives traffic so a silently-dead TCP
    /// socket surfaces as a send failure + EOF.
    public func sendPing() async {
        guard let conn = activeConn else { return }
        do {
            try await conn.sendFrame(.ping(ts: Date().timeIntervalSince1970))
        } catch {
            AnyLog.shared.info("send failed (link likely down): \(error)")
        }
    }

    /// Seconds since the last inbound frame on the active link, or nil if not
    /// linked. The heartbeat loop compares this against its deadline.
    public func secondsSinceInbound() -> Double? {
        activeConn == nil ? nil : monotonicNow() - lastInboundAt
    }

    /// Drop a link that has gone silent — half-open socket: the peer slept or
    /// vanished without RST/FIN, so our sends never error and the parked
    /// receive never wakes. Cancelling the connection wakes that receive, the
    /// session loop tears down (clearing activeConn/peer state) and the
    /// reconnect loop takes over. No-op if already unlinked.
    public func dropStaleLink(idleSeconds: Double) {
        guard let conn = activeConn else { return }
        AnyLog.shared.info(
            "link to \(peerName ?? "?") idle \(Int(idleSeconds))s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect")
        conn.cancel()
    }

    public func sendClip(_ payload: ClipPayload) async {
        guard let conn = activeConn else { return }
        let msg = WireMessage.clip(payload, ts: Date().timeIntervalSince1970)
        do {
            try await conn.sendFrame(msg)
        } catch let error as WireFrameError {
            AnyLog.shared.warning("payload too large, dropping: \(error)")
        } catch {
            AnyLog.shared.info("send failed (link likely down): \(error)")
        }
    }

    /// Drop the link + listener. Safe to call multiple times.
    public func shutdown() {
        activeConn?.cancel()
        activeConn = nil
        peerNodeID = nil
        peerName = nil
        peerProtocolMinor = 0
        listener?.cancel()
        listener = nil
        isServing = false
    }
}
