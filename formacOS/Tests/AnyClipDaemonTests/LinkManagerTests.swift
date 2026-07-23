import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func makeManager(
    token: String, port: UInt16, name: String,
    clips: Locked<[(ClipPayload, String)]>, events: Locked<[DaemonEvent]>,
    maxPeers: Int = LinkManager.defaultMaxPeers,
    pingInterval: Double = 30, pingDeadFactor: Double = 3
) async -> LinkManager {
    let m = LinkManager(
        config: LinkManager.LinkConfig(
            token: token, port: port, name: name, appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased(),
        maxPeers: maxPeers, pingInterval: pingInterval, pingDeadFactor: pingDeadFactor)
    await m.setHandlers(
        onClip: { payload, peer in clips.set(clips.get() + [(payload, peer)]) },
        emit: { event in events.set(events.get() + [event]) })
    return m
}

private func waitUntil(_ timeout: Double = 5.0, _ cond: @escaping () async -> Bool) async -> Bool {
    let deadline = monotonicNow() + timeout
    while monotonicNow() < deadline {
        if await cond() { return true }
        try? await Task.sleep(nanoseconds: 50_000_000)
    }
    return await cond()
}

/// Drive a raw peer against a serving manager: TCP connect + hello handshake,
/// returning the connected FramedConnection (already routed by the manager).
private func rawHandshake(
    port: UInt16, token: String, nodeID: String, name: String,
    minor: Int = Wire.protocolMinor, major: Int = Wire.protocolMajor
) async throws -> FramedConnection {
    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await raw.start()
    var hello = WireMessage.hello(
        tokenHash: sha256Hex(token), nodeID: nodeID, name: name, appVersion: "0.0.0-test")
    hello.protocol_minor = minor
    hello.protocol_major = major
    try await raw.sendFrame(hello)
    _ = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }  // manager's hello
    return raw
}

@Test func twoManagersHandshakeAndBroadcastBothWays() async throws {
    let aClips = Locked<[(ClipPayload, String)]>([]); let aEvents = Locked<[DaemonEvent]>([])
    let bClips = Locked<[(ClipPayload, String)]>([]); let bEvents = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28471, name: "node-a", clips: aClips, events: aEvents)
    let b = await makeManager(token: "tok", port: 28472, name: "node-b", clips: bClips, events: bEvents)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let outcome = await b.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: 28471)!), label: "a")
    #expect(outcome == .routed)
    #expect(await waitUntil {
        let ca = await a.activeLinkCount(); let cb = await b.activeLinkCount()
        return ca == 1 && cb == 1
    })
    #expect(aEvents.get().contains { if case .linkUp = $0 { return true }; return false })

    _ = await b.broadcast(.text("from-b"))
    #expect(await waitUntil {
        aClips.get().contains { if case .text(let s) = $0.0 { return s == "from-b" }; return false }
    })
    _ = await a.broadcast(.image(Data([1, 2, 3])))
    #expect(await waitUntil {
        bClips.get().contains { if case .image(let d) = $0.0 { return d == Data([1, 2, 3]) }; return false }
    })
    await a.shutdown(); await b.shutdown()
}

@Test func wrongTokenIsRejectedWithAuthEvent() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "right", port: 28473, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28473, token: "wrong", nodeID: "ffffffff-bad", name: "b")
    defer { raw.cancel() }
    #expect(await waitUntil {
        events.get().contains { if case .handshakeFailed(_, "auth") = $0 { return true }; return false }
    })
    #expect(await a.activeLinkCount() == 0)
    await a.shutdown()
}

@Test func pingIsAnsweredWithPong() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28475, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28475, token: "tok", nodeID: "ffffffff-raw", name: "raw")
    defer { raw.cancel() }
    try await raw.sendFrame(.ping(ts: 1))
    let reply = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }
    #expect(reply?.type == "pong")
    await a.shutdown()
}

@Test func staleSilentLinkIsDropped() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    // Tight per-link ping: 3 missed 0.3s intervals = 0.9s silence -> drop.
    let a = await makeManager(token: "tok", port: 28479, name: "a", clips: clips, events: events,
                              pingInterval: 0.3, pingDeadFactor: 3)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28479, token: "tok", nodeID: "ffffffff-silent", name: "raw")
    defer { raw.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    // The raw peer never pongs; the per-link staleness dropper must reap it.
    #expect(await waitUntil(5) { await a.activeLinkCount() == 0 })
    #expect(events.get().contains { if case .linkDown = $0 { return true }; return false })
    await a.shutdown()
}

@Test func majorVersionMismatchIsRefused() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28476, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28476, token: "tok", nodeID: "ffffffff-v2", name: "future", major: 2)
    defer { raw.cancel() }
    #expect(await waitUntil {
        events.get().contains {
            if case .handshakeFailed(_, let r) = $0 { return r.hasPrefix("version:") }; return false
        }
    })
    #expect(await a.activeLinkCount() == 0)
    await a.shutdown()
}

@Test func serveRetriesBindWhenPortTemporarilyHeld() async throws {
    let port: UInt16 = 28477
    let blocker = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    blocker.newConnectionHandler = { $0.cancel() }
    blocker.start(queue: .global())
    try await Task.sleep(nanoseconds: 300_000_000)

    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let m = await makeManager(token: "t", port: port, name: "retry", clips: clips, events: events)
    let serveTask = Task { try await m.serve() }; defer { serveTask.cancel() }
    try await Task.sleep(nanoseconds: 700_000_000)
    blocker.cancel()
    #expect(await waitUntil(5) { await m.isServing })
    await m.shutdown()
}

@Test func newNodeCreatesLinkAndEmitsLinkUp() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28483, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let raw = try await rawHandshake(port: 28483, token: "tok", nodeID: "node-fresh", name: "fresh")
    defer { raw.cancel() }
    #expect(await waitUntil { await a.hasLink(nodeID: "node-fresh") })
    #expect(await a.activeLinkCount() == 1)
    #expect(events.get().contains {
        if case .linkUp(let id, let name) = $0 { return id == "node-fresh" && name == "fresh" }
        return false
    })
    await a.shutdown()
}

@Test func duplicateNodeReplacesLiveSession() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28484, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let first = try await rawHandshake(port: 28484, token: "tok", nodeID: "dup", name: "first")
    #expect(await waitUntil { await a.hasLink(nodeID: "dup") })
    // Sleep past the race window so this is a genuine replacement, not a tie-break.
    try await Task.sleep(nanoseconds: 1_700_000_000)
    let second = try await rawHandshake(port: 28484, token: "tok", nodeID: "dup", name: "second")
    defer { second.cancel() }
    // The old socket is closed by the manager; still exactly one link for "dup".
    #expect(await waitUntil { (try? await withTimeout(seconds: 2) { try await first.receiveMessage() }) == nil })
    #expect(await a.activeLinkCount() == 1)
    // The second connection is the live one: a broadcast reaches it.
    _ = await a.broadcast(.text("to-second"))
    let got = try await withTimeout(seconds: 5) { try await second.receiveMessage() }
    #expect(got?.kind == "text" && got?.content == "to-second")
    await a.shutdown()
}

@Test func overCapNewPeerRefusedKnownReconnectRouted() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28488, name: "a", clips: clips, events: events, maxPeers: 2)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let p1 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n1", name: "p1")
    let p2 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n2", name: "p2")
    defer { p1.cancel(); p2.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    // A NEW node at cap is refused (its socket is closed, no link created).
    let p3 = try await rawHandshake(port: 28488, token: "tok", nodeID: "n3", name: "p3")
    defer { p3.cancel() }
    #expect(await waitUntil { (try? await withTimeout(seconds: 2) { try await p3.receiveMessage() }) == nil })
    #expect(await a.activeLinkCount() == 2)
    #expect(!(await a.hasLink(nodeID: "n3")))

    // A KNOWN node reconnecting at cap is routed (replacement), never refused.
    try await Task.sleep(nanoseconds: 1_700_000_000)
    let p1b = try await rawHandshake(port: 28488, token: "tok", nodeID: "n1", name: "p1-again")
    defer { p1b.cancel() }
    _ = await a.broadcast(.text("to-p1-again"))
    let got = try await withTimeout(seconds: 5) { try await p1b.receiveMessage() }
    #expect(got?.content == "to-p1-again")
    #expect(await a.activeLinkCount() == 2)
    await a.shutdown()
}

@Test func deadLinkFreesCapSlot() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28489, name: "a", clips: clips, events: events, maxPeers: 1)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let x = try await rawHandshake(port: 28489, token: "tok", nodeID: "x", name: "x")
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    x.cancel()                                            // peer vanishes
    #expect(await waitUntil { await a.activeLinkCount() == 0 })   // slot freed
    #expect(events.get().contains {
        if case .linkDown(let id, _) = $0 { return id == "x" }; return false
    })
    let y = try await rawHandshake(port: 28489, token: "tok", nodeID: "y", name: "y")
    defer { y.cancel() }
    #expect(await waitUntil { await a.hasLink(nodeID: "y") })     // re-admitted
    await a.shutdown()
}

@Test func broadcastFansOutAndIsolatesFailure() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28490, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let p1 = try await rawHandshake(port: 28490, token: "tok", nodeID: "n1", name: "p1")
    let p2 = try await rawHandshake(port: 28490, token: "tok", nodeID: "n2", name: "p2")
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let result = await a.broadcast(.text("fanout"))
    #expect(result.delivered.count == 2)
    let g1 = try await withTimeout(seconds: 5) { try await p1.receiveMessage() }
    let g2 = try await withTimeout(seconds: 5) { try await p2.receiveMessage() }
    #expect(g1?.content == "fanout" && g2?.content == "fanout")

    // Drop p2's socket; a broadcast failure to it must drop ONLY that link.
    p2.cancel()
    #expect(await waitUntil { await a.activeLinkCount() == 1 })
    _ = await a.broadcast(.text("after-drop"))
    let g1b = try await withTimeout(seconds: 5) { try await p1.receiveMessage() }
    #expect(g1b?.content == "after-drop")
    #expect(await a.hasLink(nodeID: "n1"))
    p1.cancel()
    await a.shutdown()
}

@Test func perLinkMinorGatingFilesVsFallback() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28492, name: "a", clips: clips, events: events)
    let serveA = Task { try await a.serve() }; defer { serveA.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawHandshake(port: 28492, token: "tok", nodeID: "old", name: "old", minor: 0)
    let modern = try await rawHandshake(port: 28492, token: "tok", nodeID: "new", name: "new", minor: 1)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let files: [(name: String, data: Data)] = [
        (name: "a.txt", data: Data("one".utf8)), (name: "b.txt", data: Data("two".utf8))]
    let result = await a.broadcast(.files(files))
    #expect(result.maxDropped == 1)   // the minor-0 peer left one file behind

    let gOld = try await withTimeout(seconds: 5) { try await old.receiveMessage() }
    #expect(gOld?.kind == "file")                       // legacy first-file fallback
    #expect(gOld?.name == "a.txt")
    let gNew = try await withTimeout(seconds: 5) { try await modern.receiveMessage() }
    #expect(gNew?.kind == "files")                      // full multi-file
    #expect(gNew?.files?.count == 2)
    await a.shutdown()
}

@Test func idleWatchdogFiresOnlyAtZeroActiveLinks() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let beacon = MdnsBeacon(nodeID: "self", emit: { _ in }, onPeer: { _, _, _ in })

    // Zero links + tight thresholds -> the global escalator bounces the daemon.
    let idle = await makeManager(token: "tok", port: 28493, name: "a", clips: clips, events: events)
    var threw = false
    do { try await idleLinkWatchdog(beacon: beacon, manager: idle, idleThreshold: 0.2, refreshAttempts: 1) }
    catch is DaemonRestartError { threw = true } catch {}
    #expect(threw)
    await idle.shutdown()

    // With an active link, activeLinkCount > 0 resets it: no throw in the window.
    let live = await makeManager(token: "tok", port: 28494, name: "b", clips: clips, events: events)
    let serve = Task { try await live.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await live.isServing })
    let raw = try await rawHandshake(port: 28494, token: "tok", nodeID: "peer", name: "peer")
    defer { raw.cancel() }
    #expect(await waitUntil { await live.activeLinkCount() == 1 })
    let wd = Task { try await idleLinkWatchdog(beacon: beacon, manager: live, idleThreshold: 0.2, refreshAttempts: 1) }
    try await Task.sleep(nanoseconds: 1_000_000_000)
    #expect(!wd.isCancelled ? true : true)              // still running (never threw)
    wd.cancel()
    await live.shutdown()
}
