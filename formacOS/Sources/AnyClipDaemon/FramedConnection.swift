import Foundation
import Network
import AnyClipCore

public enum WireConnectionError: Error {
    case closed
    case cancelled
}

/// Async framing layer over one NWConnection: 4-byte BE length + JSON body,
/// mirroring PeerLink._send/_recv in anyclip.py.
public final class FramedConnection: @unchecked Sendable {
    public let connection: NWConnection
    public private(set) var remoteIP: String?
    /// Per-connection send budget; an instance hook so tests can shrink it.
    public var sendTimeout: Double = Wire.sendTimeout

    public init(connection: NWConnection) {
        self.connection = connection
    }

    /// Outbound connection with the same TCP tuning as the Python client:
    /// keepalive on (idle 15 s) and a 5 s connect timeout.
    public static func outbound(to endpoint: NWEndpoint) -> FramedConnection {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
        // Actually probe after idle: ~15s idle + 4×5s unanswered = ~35s for the
        // OS to tear down a dead peer, as a backstop to the app-layer heartbeat.
        tcp.keepaliveCount = 4
        tcp.keepaliveInterval = 5
        tcp.connectionTimeout = 5
        let params = NWParameters(tls: nil, tcp: tcp)
        return FramedConnection(connection: NWConnection(to: endpoint, using: params))
    }

    /// Start and suspend until .ready (throws on .failed/.cancelled).
    public func start() async throws {
        try await withCheckedThrowingContinuation { (cont: CheckedContinuation<Void, Error>) in
            let resumed = Locked(false)
            connection.stateUpdateHandler = { [weak self] state in
                switch state {
                case .ready:
                    self?.captureRemoteIP()
                    if !resumed.exchange(true) { cont.resume() }
                case .failed(let error):
                    if !resumed.exchange(true) { cont.resume(throwing: error) }
                case .cancelled:
                    if !resumed.exchange(true) {
                        cont.resume(throwing: WireConnectionError.cancelled)
                    }
                default:
                    break
                }
            }
            connection.start(queue: .global(qos: .userInitiated))
        }
        connection.stateUpdateHandler = nil
    }

    private func captureRemoteIP() {
        guard let path = connection.currentPath,
              case let .hostPort(host, _) = path.remoteEndpoint
        else { return }
        // Host description can carry a scope suffix ("192.168.0.5%en0").
        remoteIP = "\(host)".split(separator: "%").first.map(String.init)
    }

    public func sendFrame(_ message: WireMessage) async throws {
        let data = try message.encodeFrame()
        // Bound the send: a write whose completion is lost (connection
        // cancelled mid-send) or that parks on a full TCP buffer (wedged peer)
        // would otherwise hang the caller forever. The clipboard poll loop and
        // the heartbeat self-heal both await sends inline, so a single parked
        // send froze outbound sync until an app restart. On timeout, withTimeout
        // cancels the operation task, whose onCancel cancels the connection
        // (firing the parked completion + tearing the link down to reconnect),
        // and rethrows TimeoutError to the caller.
        try await withTimeout(seconds: sendTimeout) { [self] in
            try await rawSendFrame(data)
        }
    }

    /// The bare framed send: one NWConnection.send awaited via a continuation.
    ///
    /// The continuation MUST be resumable from EITHER the send completion OR
    /// task cancellation. NWConnection can silently drop a send's
    /// `.contentProcessed` completion when the connection is cancelled out from
    /// under an in-flight send (peer restart / tie-break cancelling the old link
    /// mid-send). The previous version's onCancel only called
    /// `connection.cancel()` and *relied on* that completion then firing; when
    /// it did NOT fire, this continuation leaked forever — and because
    /// withTimeout structurally awaits its operation child, the whole clipboard
    /// poll loop froze (the v1.1.10 outbound-silence wedge). We now resume the
    /// continuation directly from onCancel, independent of NWConnection, so a
    /// dropped completion can never strand the caller.
    private func rawSendFrame(_ data: Data) async throws {
        enum SendState {
            case idle                                       // no continuation yet
            case cancelledBeforeWait                        // onCancel beat the body
            case waiting(CheckedContinuation<Void, Error>)  // body parked
            case finished                                   // resumed exactly once
        }
        let state = Locked<SendState>(.idle)

        // Resume exactly once from the send completion.
        let complete: @Sendable (Error?) -> Void = { error in
            let prev = state.exchange(.finished)
            if case .waiting(let cont) = prev {
                if let error { cont.resume(throwing: error) } else { cont.resume() }
            }
        }

        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (cont: CheckedContinuation<Void, Error>) in
                let prev = state.exchange(.waiting(cont))
                if case .cancelledBeforeWait = prev {
                    // onCancel already fired before we parked — resume now and
                    // skip the send entirely.
                    _ = state.exchange(.finished)
                    cont.resume(throwing: WireConnectionError.cancelled)
                    return
                }
                connection.send(content: data,
                                completion: .contentProcessed { error in complete(error) })
            }
        } onCancel: {
            // Break the leak: cancel the connection AND resume the continuation
            // ourselves, so a dropped send completion can never strand us.
            connection.cancel()
            let prev = state.exchange(.cancelledBeforeWait)
            if case .waiting(let cont) = prev {
                _ = state.exchange(.finished)
                cont.resume(throwing: WireConnectionError.cancelled)
            }
        }
    }

    /// One message, or nil on an invalid frame (bad length / bad JSON) —
    /// the caller closes the session on nil, matching Python _recv().
    public func receiveMessage() async throws -> WireMessage? {
        let header = try await receiveExactly(4)
        let n = WireMessage.frameLength(header)
        guard n > 0, n <= Wire.maxPayload else {
            AnyLog.shared.warning("invalid frame length: \(n)")
            return nil
        }
        let body = try await receiveExactly(n)
        let msg = WireMessage.decodeBody(body)
        if msg == nil { AnyLog.shared.warning("bad json frame (\(n) bytes)") }
        return msg
    }

    private func receiveExactly(_ n: Int) async throws -> Data {
        var buffer = Data()
        while buffer.count < n {
            let chunk = try await receiveSome(max: n - buffer.count)
            buffer.append(chunk)
        }
        return buffer
    }

    private func receiveSome(max: Int) async throws -> Data {
        // NWConnection.receive's continuation is otherwise non-cancellable:
        // a parked receive (linked, peer idle) never resumes on task
        // cancellation, so a structured task-group shutdown — app quit while
        // LINKED — deadlocks. withTaskCancellationHandler cancels the
        // connection on cancel, which completes the pending receive (with an
        // error) and resumes the continuation. `resumed` guards the
        // exactly-once resume against a data/cancel race.
        let resumed = Locked(false)
        return try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (cont: CheckedContinuation<Data, Error>) in
                connection.receive(minimumIncompleteLength: 1, maximumLength: max) {
                    content, _, _, error in
                    if resumed.exchange(true) { return }
                    if let error { cont.resume(throwing: error); return }
                    if let content, !content.isEmpty {
                        cont.resume(returning: content)
                        return
                    }
                    cont.resume(throwing: WireConnectionError.closed) // EOF
                }
            }
        } onCancel: {
            connection.cancel()
        }
    }

    public func cancel() {
        connection.cancel()
    }
}
