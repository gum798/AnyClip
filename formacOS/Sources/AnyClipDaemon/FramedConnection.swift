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

    public init(connection: NWConnection) {
        self.connection = connection
    }

    /// Outbound connection with the same TCP tuning as the Python client:
    /// keepalive on (idle 15 s) and a 5 s connect timeout.
    public static func outbound(to endpoint: NWEndpoint) -> FramedConnection {
        let tcp = NWProtocolTCP.Options()
        tcp.enableKeepalive = true
        tcp.keepaliveIdle = 15
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
        // Same non-cancellable hazard as receiveSome: a send parked on a full
        // TCP buffer (wedged/throttled peer) would otherwise ignore task
        // cancellation and stall a structured shutdown. Cancel the connection
        // on cancel so the send completion fires; resumed-guard the
        // exactly-once resume against a completion/cancel race.
        let resumed = Locked(false)
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation {
                (cont: CheckedContinuation<Void, Error>) in
                connection.send(content: data, completion: .contentProcessed { error in
                    if resumed.exchange(true) { return }
                    if let error { cont.resume(throwing: error) } else { cont.resume() }
                })
            }
        } onCancel: {
            connection.cancel()
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
