import Foundation
import Network
import AnyClipCore

/// One peer pair: the session lifecycle from the POST-hello point. The
/// listening socket, outbound dialing, hello exchange, gate, and routing now
/// live in LinkManager; a PeerLink is constructed with an already-handshaked
/// FramedConnection and the parsed hello, and never re-reads a hello. Port of
/// the narrowed anyclip.PeerLink.
public actor PeerLink {
    /// Peer identity from the handed-over hello — immutable for the link's
    /// lifetime, so nonisolated lets the broadcast loop read them without await.
    public nonisolated let peerNodeID: String
    public nonisolated let peerName: String
    /// Peer's advertised protocol minor; gates the outbound files/kind:"file"
    /// downgrade in LinkManager's broadcast loop.
    public nonisolated let peerProtocolMinor: Int

    private let conn: FramedConnection
    private let onClip: @Sendable (ClipPayload, String) async -> Void
    /// Monotonic timestamp of the last inbound frame. Drives half-open
    /// detection: a slept/vanished peer keeps the socket "writable" yet sends
    /// nothing back, so staleness is judged from inbound silence.
    private var lastInboundAt: Double = monotonicNow()
    private var closed = false

    public init(
        conn: FramedConnection, peerNodeID: String, peerName: String,
        peerProtocolMinor: Int,
        onClip: @escaping @Sendable (ClipPayload, String) async -> Void
    ) {
        self.conn = conn
        self.peerNodeID = peerNodeID
        self.peerName = peerName
        self.peerProtocolMinor = peerProtocolMinor
        self.onClip = onClip
    }

    public var isActive: Bool { !closed }

    /// Receive loop. Returns when the socket EOFs/errors or close() cancels it.
    /// Emits NO lifecycle events — LinkManager owns link up/down.
    public func run() async {
        while !closed {
            let msg: WireMessage?
            do { msg = try await conn.receiveMessage() } catch { break }
            lastInboundAt = monotonicNow()
            guard let m = msg else { break }
            switch m.type {
            case "clip":
                await handleClip(m)
            case "ping":
                do { try await conn.sendFrame(.pong(ts: Date().timeIntervalSince1970)) }
                catch { AnyLog.shared.info("send failed (link likely down): \(error)") }
            case "pong":
                break
            default:
                AnyLog.shared.debug("ignoring message type: \(m.type)")
            }
        }
        closed = true
        AnyLog.shared.info("peer disconnected name=\(peerName) id=\(peerNodeID.prefix(8))")
    }

    private func handleClip(_ m: WireMessage) async {
        let kind = m.kind ?? "text"
        switch kind {
        case "text":
            if let content = m.content { await onClip(.text(content), peerName) }
        case "image":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad image payload from peer"); return
            }
            await onClip(.image(data), peerName)
        case "file":
            guard let content = m.content else { return }
            guard let data = strictBase64Decode(content) else {
                AnyLog.shared.warning("bad file payload from peer"); return
            }
            let name = (m.name?.isEmpty == false) ? m.name! : "received.bin"
            await onClip(.file(name: name, data: data), peerName)
        case "files":
            guard let entries = decodeFileEntries(m.files) else {
                AnyLog.shared.warning(
                    "bad files payload from peer (empty or invalid base64); dropping frame")
                return
            }
            await onClip(.files(entries), peerName)
        default:
            AnyLog.shared.debug("ignoring clip with kind=\(kind)")
        }
    }

    /// Per-link broadcast send. Returns false only on a "link likely down"
    /// error, so the caller drops this link; an oversize payload keeps the link.
    public func sendClip(_ payload: ClipPayload) async -> Bool {
        if closed { return false }
        let msg = WireMessage.clip(payload, ts: Date().timeIntervalSince1970)
        do { try await conn.sendFrame(msg); return true }
        catch let error as WireFrameError {
            AnyLog.shared.warning("payload too large, dropping: \(error)"); return true
        }
        catch {
            AnyLog.shared.info("send failed (link likely down): \(error)"); return false
        }
    }

    /// App-layer keepalive; drives traffic so a silently-dead socket surfaces.
    public func sendPing() async {
        if closed { return }
        do { try await conn.sendFrame(.ping(ts: Date().timeIntervalSince1970)) }
        catch { AnyLog.shared.info("send failed (link likely down): \(error)") }
    }

    /// Seconds since the last inbound frame, or nil once closed.
    public func secondsSinceInbound() -> Double? {
        closed ? nil : monotonicNow() - lastInboundAt
    }

    /// Drop a half-open link (peer slept/vanished): cancelling the connection
    /// wakes the parked receive, run() tears down, and LinkManager reaps it.
    public func dropStaleLink(idleSeconds: Double) {
        guard !closed else { return }
        AnyLog.shared.info(
            "link to \(peerName) idle \(Int(idleSeconds))s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect")
        closed = true
        conn.cancel()
    }

    /// Cancel the underlying connection from any isolation domain (routing /
    /// broadcast / shutdown call this synchronously). run() observes the
    /// cancelled socket and sets `closed`.
    public nonisolated func close() {
        conn.cancel()
    }
}
