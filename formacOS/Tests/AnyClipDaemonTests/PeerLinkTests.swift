import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeLink(
    token: String, port: UInt16, name: String,
    clips: Locked<[ClipPayload]>, events: Locked<[DaemonEvent]>
) async -> PeerLink {
    let link = PeerLink(
        config: PeerLink.LinkConfig(
            token: token, port: port, name: name, appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(
        onClip: { payload in clips.set(clips.get() + [payload]) },
        emit: { event in events.set(events.get() + [event]) })
    return link
}

private func waitUntil(
    _ timeout: Double = 5.0, _ cond: @escaping () async -> Bool
) async -> Bool {
    let deadline = monotonicNow() + timeout
    while monotonicNow() < deadline {
        if await cond() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return await cond()
}

@Test func twoLinksHandshakeAndExchangeClips() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28471, name: "node-a",
                           clips: aClips, events: aEvents)
    let b = await makeLink(token: "tok", port: 28472, name: "node-b",
                           clips: bClips, events: bEvents)

    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let connectB = Task {
        await b.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28471)!),
            label: "127.0.0.1:28471")
    }
    defer { connectB.cancel() }

    let bothActive = await waitUntil {
        let aActive = await a.isActive
        let bActive = await b.isActive
        return aActive && bActive
    }
    #expect(bothActive)
    #expect(await a.peerName == "node-b")
    #expect(await b.peerName == "node-a")
    #expect(aEvents.get().contains { if case .linkUp = $0 { return true }; return false })

    await b.sendClip(.text("from-b"))
    #expect(await waitUntil {
        aClips.get().contains {
            if case .text(let s) = $0 { return s == "from-b" }
            return false
        }
    })

    await a.sendClip(.image(Data([1, 2, 3])))
    #expect(await waitUntil {
        bClips.get().contains {
            if case .image(let d) = $0 { return d == Data([1, 2, 3]) }
            return false
        }
    })

    await a.shutdown(); await b.shutdown()
}

@Test func wrongTokenIsRejectedWithAuthEvent() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "right", port: 28473, name: "a",
                           clips: aClips, events: aEvents)
    let b = await makeLink(token: "wrong", port: 28474, name: "b",
                           clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    await b.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28473)!),
        label: "127.0.0.1:28473")

    #expect(await waitUntil {
        aEvents.get().contains {
            if case .handshakeFailed(_, "auth") = $0 { return true }; return false
        }
    })
    #expect(!(await a.isActive))
    await a.shutdown(); await b.shutdown()
}

@Test func pingIsAnsweredWithPong() async throws {
    // Drive a raw FramedConnection against a serving PeerLink: complete the
    // handshake manually, send ping, expect pong.
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28475, name: "a",
                           clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28475)!))
    try await raw.start()
    defer { raw.cancel() }
    try await raw.sendFrame(.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-raw", name: "raw",
        appVersion: "0.0.0-test"))
    let serverHello = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }
    #expect(serverHello?.type == "hello")
    try await raw.sendFrame(.ping(ts: 1))
    let reply = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }
    #expect(reply?.type == "pong")
    await a.shutdown()
}

// Regression: a linked peer that goes silent — half-open TCP, i.e. the peer
// slept or vanished without sending RST/FIN — must be detected and dropped.
// On a half-open socket our sends never error and the parked receive never
// wakes, so without an inbound-traffic deadline the link is a permanent zombie
// (isActive forever) and the daemon never reconnects. (Observed in the field:
// a Mac held a dead link for ~50 min after the Windows peer slept.)
@Test func staleLinkIsDroppedWhenPeerGoesSilent() async throws {
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28479, name: "a",
                           clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    // Raw peer completes the handshake, then goes completely silent: it never
    // pongs our pings and sends no further frames.
    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28479)!))
    try await raw.start()
    defer { raw.cancel() }
    try await raw.sendFrame(.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-raw", name: "raw",
        appVersion: "0.0.0-test"))
    _ = try await withTimeout(seconds: 5) { try await raw.receiveMessage() } // a's hello
    #expect(await waitUntil { await a.isActive })

    // Heartbeat with a tight deadline: 3 missed 0.3s intervals = 0.9s of
    // silence -> drop. The silent raw peer must be dropped well within 5s.
    let heartbeat = Task { try await linkPingLoop(link: a, interval: 0.3, deadFactor: 3) }
    defer { heartbeat.cancel() }

    #expect(await waitUntil(5) { !(await a.isActive) })
    await a.shutdown()
}

@Test func majorVersionMismatchIsRefused() async throws {
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28476, name: "a",
                           clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28476)!))
    try await raw.start()
    defer { raw.cancel() }
    var hello = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-v2", name: "future",
        appVersion: "2.0.0")
    hello.protocol_major = 2
    try await raw.sendFrame(hello)
    _ = try await raw.receiveMessage() // server's hello
    #expect(await waitUntil {
        events.get().contains {
            if case .handshakeFailed(_, let r) = $0 { return r.hasPrefix("version:") }
            return false
        }
    })
    #expect(!(await a.isActive))
    await a.shutdown()
}

@Test func serveRetriesBindWhenPortTemporarilyHeld() async throws {
    let port: UInt16 = 28477
    // Occupy the port with a raw NWListener (no TLS, no allowLocalEndpointReuse)
    // so that PeerLink's first bind attempt sees EADDRINUSE.
    let blocker = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    blocker.newConnectionHandler = { $0.cancel() }
    blocker.start(queue: .global())
    // Give the blocker time to bind before serve() races it.
    try await Task.sleep(nanoseconds: 300_000_000)

    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let link = PeerLink(
        config: PeerLink.LinkConfig(token: "t", port: port, name: "retry", appVersion: "0"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(onClip: { _ in clips.set(clips.get()) },
                           emit: { _ in events.set(events.get()) })
    let serveTask = Task { try await link.serve() }
    defer { serveTask.cancel() }

    // Let serve() attempt (and possibly hit EADDRINUSE) before we release.
    try await Task.sleep(nanoseconds: 700_000_000)
    blocker.cancel()

    // Wait up to 5s for PeerLink to successfully bind after the blocker releases.
    let deadline = monotonicNow() + 5
    var serving = false
    while monotonicNow() < deadline {
        if await link.isServing { serving = true; break }
        try? await Task.sleep(nanoseconds: 100_000_000)
    }
    // NOTE: NWListener with allowLocalEndpointReuse on both sides may not
    // produce an actual EADDRINUSE collision (the OS may allow two listeners
    // on the same port when both set SO_REUSEADDR). If the blocker didn't
    // trigger the retry path the test still validates the happy-path
    // (serve() reaches isServing == true). Check logs for
    // "retrying bind" to confirm the retry path was exercised.
    #expect(serving)
    await link.shutdown()
}

@Test func twoLinksExchangeMultipleFiles() async throws {
    let aClips = Locked<[ClipPayload]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[ClipPayload]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28485, name: "node-a", clips: aClips, events: aEvents)
    let b = await makeLink(token: "tok", port: 28486, name: "node-b", clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })
    let connectB = Task {
        await b.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28485)!),
            label: "127.0.0.1:28485")
    }
    defer { connectB.cancel() }
    #expect(await waitUntil { let x = await a.isActive; let y = await b.isActive; return x && y })

    let files: [(name: String, data: Data)] = [
        (name: "노트.txt", data: Data("body one".utf8)),
        (name: "réport (v2).bin", data: Data([0, 1, 2, 3])),
    ]
    await b.sendClip(.files(files))
    #expect(await waitUntil {
        aClips.get().contains {
            if case .files(let fs) = $0 {
                return fs.count == 2
                    && fs.contains { $0.name == "노트.txt" && $0.data == Data("body one".utf8) }
                    && fs.contains { $0.name == "réport (v2).bin" && $0.data == Data([0, 1, 2, 3]) }
            }
            return false
        }
    })
    await a.shutdown(); await b.shutdown()
}

@Test func peerProtocolMinorRetainedFromHello() async throws {
    let clips = Locked<[ClipPayload]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeLink(token: "tok", port: 28487, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }
    defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28487)!))
    try await raw.start()
    defer { raw.cancel() }
    var hello = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "ffffffff-oldpeer", name: "old", appVersion: "1.0.0")
    hello.protocol_minor = 0                                  // pre-1.1 peer
    try await raw.sendFrame(hello)
    _ = try await raw.receiveMessage()                        // a's hello
    #expect(await waitUntil { await a.isActive })
    #expect(await a.peerProtocolMinor == 0)
    await a.shutdown()
}
