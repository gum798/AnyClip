import Foundation

/// Startup self-diagnosis for the macOS Local Network permission.
/// Port of permission_probe.py: 0 mDNS events in the observation window
/// while a network interface exists => the permission is likely revoked.
public enum ProbeResult: String, Sendable {
    case ok
    case blockedLocalNetwork = "blocked_local_network"
    case noNetwork = "no_network"
}

public func decideProbe(eventsSeen: Int, hasNetwork: Bool) -> ProbeResult {
    if !hasNetwork { return .noNetwork }
    if eventsSeen <= 0 { return .blockedLocalNetwork }
    return .ok
}

public func runProbe(
    eventsSeen: @escaping @Sendable () async -> Int,
    hasNetwork: @escaping @Sendable () -> Bool,
    waitSeconds: Double = 30.0
) async throws -> ProbeResult {
    try await Task.sleep(nanoseconds: UInt64(waitSeconds * 1_000_000_000))
    return decideProbe(eventsSeen: await eventsSeen(), hasNetwork: hasNetwork())
}
