import Foundation

/// Per-IP cooldown after repeated handshake failures. After maxFails failed
/// handshakes from the same IP, that IP is blocked for cooldown seconds.
/// A successful handshake clears the counter. Stale entries are swept lazily.
/// Port of anyclip.AuthGate; the caller (PeerLink actor) provides isolation.
public struct AuthGate: Sendable {
    public static let maxFails = 5
    public static let cooldown: Double = 60.0

    private struct Entry: Sendable {
        var count: Int
        var last: Double
    }

    private var fails: [String: Entry] = [:]
    private let now: @Sendable () -> Double

    public init(now: @escaping @Sendable () -> Double = { Date().timeIntervalSince1970 }) {
        self.now = now
    }

    public func isBlocked(_ ip: String) -> Bool {
        let t = now()
        guard let entry = fails[ip] else { return false }
        // Entry is stale (past cooldown window) → treat as not blocked.
        if t - entry.last >= Self.cooldown { return false }
        return entry.count >= Self.maxFails
    }

    public mutating func recordFail(_ ip: String) {
        let count = fails[ip]?.count ?? 0
        fails[ip] = Entry(count: count + 1, last: now())
        sweep()
    }

    public mutating func recordOK(_ ip: String) {
        fails[ip] = nil
    }

    private mutating func sweep() {
        let t = now()
        fails = fails.filter { t - $0.value.last < Self.cooldown }
    }
}
