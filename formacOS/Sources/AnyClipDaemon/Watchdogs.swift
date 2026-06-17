import Foundation
import AnyClipCore

/// Thrown by watchdogs to unwind the task group; the in-process supervisor
/// restarts the daemon with backoff (Python: RuntimeError -> supervisor).
public struct DaemonRestartError: Error, CustomStringConvertible {
    public let message: String
    public var description: String { message }
    public init(_ message: String) { self.message = message }
}

func sleepSeconds(_ seconds: Double) async throws {
    try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
}

/// App-layer heartbeat while linked. Two jobs:
///  1. Ping every `interval`s, so an *actively broken* socket surfaces as a
///     send failure + EOF.
///  2. Enforce a liveness deadline. A half-open socket — the peer slept or
///     vanished without RST/FIN — accepts our pings silently and never
///     delivers EOF, so the parked receive idles forever and the link is a
///     permanent zombie. Detection therefore can't rely on send failures; we
///     require *inbound* traffic (the peer pongs our pings). If nothing
///     arrives for `interval * deadFactor`, the link is dead — drop it so the
///     reconnect loop runs. (Field bug: a Mac held a dead link for ~50 min
///     after its peer slept, which in turn made the peer idle-bounce forever.)
public func linkPingLoop(
    link: PeerLink, interval: Double = 30, deadFactor: Double = 3
) async throws {
    while true {
        try await sleepSeconds(interval)
        guard await link.isActive else { continue }
        await link.sendPing()
        if let idle = await link.secondsSinceInbound(), idle > interval * deadFactor {
            await link.dropStaleLink(idleSeconds: idle)
        }
    }
}

/// Bounce the daemon when the host IPv4 changes — the Bonjour advertisement
/// carries the old address and quietly stops working otherwise.
public func networkWatchdog(beacon: MdnsBeacon, interval: Double = 15) async throws {
    while true {
        try await sleepSeconds(interval)
        guard let previous = await beacon.advertisedIP else { continue }
        if let current = primaryIPv4(), current != previous {
            throw DaemonRestartError(
                "local IPv4 changed: \(previous) -> \(current); "
                + "restarting daemon to re-advertise mDNS")
        }
    }
}

/// Self-heal mDNS when the link sits dead too long: refresh browse +
/// re-announce up to `refreshAttempts` times, then bounce the daemon.
/// Deviation from Python: also calls link.reAnnounce() because in Swift
/// the Bonjour advertisement lives on PeerLink's NWListener (not MdnsBeacon),
/// so both sides need to be refreshed.
public func idleLinkWatchdog(
    beacon: MdnsBeacon, link: PeerLink,
    idleThreshold: Double = 60, refreshAttempts: Int = 3
) async throws {
    var consecutiveIdle = 0
    while true {
        try await sleepSeconds(idleThreshold)
        if await link.isActive {
            consecutiveIdle = 0
            continue
        }
        consecutiveIdle += 1
        if consecutiveIdle <= refreshAttempts {
            AnyLog.shared.info(
                "link idle \(Int(idleThreshold * Double(consecutiveIdle)))s; refreshing mDNS "
                + "(attempt \(consecutiveIdle)/\(refreshAttempts))")
            await beacon.refresh()
            await link.reAnnounce()
        } else {
            throw DaemonRestartError(
                "link idle with no recovery after \(refreshAttempts) mDNS refresh "
                + "attempts; bouncing daemon")
        }
    }
}

/// Retry every known mDNS peer while unlinked. Backoff 1s -> 60s; sessions
/// that lasted > 5s reset it; 3 consecutive fast fails prune the address.
public func mdnsReconnectLoop(beacon: MdnsBeacon, link: PeerLink) async throws {
    var backoff: Double = 1
    while true {
        if await link.isActive {
            backoff = 1
            try await sleepSeconds(2)
            continue
        }
        let peers = await beacon.peersSnapshot()
        if peers.isEmpty {
            try await sleepSeconds(2)
            continue
        }
        var attempted = false
        for peer in peers {
            if await link.isActive { break }
            attempted = true
            let start = monotonicNow()
            await link.tryConnect(to: peer.endpoint, label: peer.label)
            let elapsed = monotonicNow() - start
            if await link.isActive {
                await beacon.clearFails(label: peer.label)
                if elapsed > 5 { backoff = 1 }
                break
            }
            if elapsed > 5 {
                // Handshake succeeded and the session lived a while before
                // dropping — a healthy peer, not a prune candidate.
                await beacon.clearFails(label: peer.label)
                continue
            }
            let fails = await beacon.recordFail(label: peer.label)
            if fails >= Wire.maxReconnectFails {
                await beacon.pruneAddress(label: peer.label)
                AnyLog.shared.info(
                    "pruned stale peer address \(peer.label) after \(fails) failed "
                    + "attempts; awaiting fresh mDNS discovery")
            }
        }
        if await link.isActive { continue }
        if attempted {
            try await sleepSeconds(min(backoff, 60))
            backoff = min(backoff * 2, 60)
        } else {
            try await sleepSeconds(2)
        }
    }
}
