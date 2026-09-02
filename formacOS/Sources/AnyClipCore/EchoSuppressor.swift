import Foundation

/// Tracks the hash of the last item received from a peer per kind, so the
/// clipboard poller does not bounce a peer's update right back at them.
/// Text/image/file are tracked separately. Port of anyclip.EchoSuppressor.
///
/// Suppression is bounded to `suppressWindowSeconds` after the receive: the
/// mechanical echo (our own clipboard write re-observed) always lands within
/// seconds — and the watcher pre-filters it anyway (changeCount here, content
/// baselines in Python/C#), so this struct is the backstop for races. Without
/// the window, a user DELIBERATELY re-copying the exact string they last
/// received could never send it back (it hashes identically to the echo), for
/// as long as no other clip arrived.
public struct EchoSuppressor: Sendable {
    public static let suppressWindowSeconds: TimeInterval = 30.0

    private var last: [String: (hash: String, at: TimeInterval)] = [:]

    public init() {}

    public mutating func markReceived(
        kind: String, payloadHash: String,
        now: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) {
        last[kind] = (payloadHash, now)
    }

    public func shouldSend(
        kind: String, payloadHash: String,
        now: TimeInterval = ProcessInfo.processInfo.systemUptime
    ) -> Bool {
        guard let entry = last[kind], entry.hash == payloadHash else { return true }
        return now - entry.at > Self.suppressWindowSeconds
    }
}
