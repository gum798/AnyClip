/// Tracks the hash of the last item received from a peer per kind, so the
/// clipboard poller does not bounce a peer's update right back at them.
/// Text/image/file are tracked separately. Port of anyclip.EchoSuppressor.
public struct EchoSuppressor: Sendable {
    private var last: [String: String] = [:]

    public init() {}

    public mutating func markReceived(kind: String, payloadHash: String) {
        last[kind] = payloadHash
    }

    public func shouldSend(kind: String, payloadHash: String) -> Bool {
        last[kind] != payloadHash
    }
}
