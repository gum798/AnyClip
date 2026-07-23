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

/// Self-heal mDNS when NO link is active for too long: refresh browse +
/// re-announce up to `refreshAttempts` times, then bounce the daemon. Keys on
/// the manager's active-link count, never on a single link's idleness — keyed
/// per-link, one sleeping peer would bounce the daemon and tear down every
/// healthy link (spec: global escalator fires only at zero active links).
public func idleLinkWatchdog(
    beacon: MdnsBeacon, manager: LinkManager,
    idleThreshold: Double = 60, refreshAttempts: Int = 3
) async throws {
    var consecutiveIdle = 0
    while true {
        try await sleepSeconds(idleThreshold)
        if await manager.activeLinkCount() > 0 {
            consecutiveIdle = 0
            continue
        }
        consecutiveIdle += 1
        if consecutiveIdle <= refreshAttempts {
            AnyLog.shared.info(
                "no active links for \(Int(idleThreshold * Double(consecutiveIdle)))s; "
                + "refreshing mDNS (attempt \(consecutiveIdle)/\(refreshAttempts))")
            await beacon.refresh()
            await manager.reAnnounce()
        } else {
            throw DaemonRestartError(
                "no active links after \(refreshAttempts) mDNS refresh attempts; "
                + "bouncing daemon")
        }
    }
}

/// Ensure a link to every discovered peer (up to the cap). Keyed per address on
/// the beacon; the manager's node_id table de-dupes so an already-meshed peer is
/// never re-dialed. Re-admission after a drop happens here on the next pass (no
/// new timer). Backoff is per-address via recordFail/pruneAddress.
public func mdnsReconnectLoop(beacon: MdnsBeacon, manager: LinkManager) async throws {
    while true {
        let peers = await beacon.peersSnapshot()
        for peer in peers {
            if await manager.atCap { break }               // no free slots this pass
            if await manager.hasLink(nodeID: peer.peerID) { continue }  // already meshed
            let outcome = await manager.tryConnect(to: peer.endpoint, label: peer.label)
            switch outcome {
            case .routed:
                await beacon.clearFails(label: peer.label)
            case .atCap, .busy:
                break                                       // don't penalise the address
            case .failed:
                let fails = await beacon.recordFail(label: peer.label)
                if fails >= Wire.maxReconnectFails {
                    await beacon.pruneAddress(label: peer.label)
                    AnyLog.shared.info(
                        "pruned stale peer address \(peer.label) after \(fails) failed "
                        + "attempts; awaiting fresh mDNS discovery")
                }
            }
        }
        try await sleepSeconds(2)
    }
}
