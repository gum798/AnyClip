import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

/// 64 MiB wire frames (protocol 1.2) and the per-link legacy send gate.
///
/// The frame cap moved 16 MiB -> 64 MiB so a ~16 MB pptx syncs. Peers still on
/// protocol < 1.2 enforce the old 16 MiB receive cap and CLOSE the session on a
/// bigger frame, so the broadcast fan-out must gate per link: encode the payload
/// variant chosen for that link once, and skip (never drop) any link whose peer
/// minor is < 2 when that frame exceeds Wire.legacyMaxPayload.
/// Mirrors tests/test_large_frames.py on the Python side.

private func makeManager(
    token: String, port: UInt16, name: String,
    clips: Locked<[(ClipPayload, String)]>, events: Locked<[DaemonEvent]>
) async -> LinkManager {
    let m = LinkManager(
        config: LinkManager.LinkConfig(
            token: token, port: port, name: name, appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
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

/// TCP connect + hello handshake against a serving manager, advertising `minor`.
private func rawPeer(
    port: UInt16, token: String, nodeID: String, name: String, minor: Int
) async throws -> FramedConnection {
    let raw = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await raw.start()
    var hello = WireMessage.hello(
        tokenHash: sha256Hex(token), nodeID: nodeID, name: name, appVersion: "0.0.0-test")
    hello.protocol_minor = minor
    try await raw.sendFrame(hello)
    _ = try await withTimeout(seconds: 5) { try await raw.receiveMessage() }  // manager's hello
    return raw
}

/// Point the shared logger at ONE throwaway file so the gate's exact log line
/// can be asserted (the daemon logs through AnyLog.shared, like the live app).
/// A global is initialized exactly once, so parallel tests cannot re-point the
/// shared logger out from under each other mid-run — hence the distinct peer
/// names below, since every test reads the same file.
private let sharedGateLogURL: URL = {
    let dir = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-gatelog-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    let url = dir.appendingPathComponent("anyclip.log")
    AnyLog.shared.configure(fileURL: url, verbose: false)
    return url
}()

private func sharedLogText() -> String {
    AnyLog.shared.flushForTesting()
    return (try? String(contentsOf: sharedGateLogURL, encoding: .utf8)) ?? ""
}

/// A text payload whose encoded frame is guaranteed to exceed the legacy cap.
private func overLegacyCapText() -> String {
    String(repeating: "x", count: Wire.legacyMaxPayload + 1024)
}

/// Drain one frame from `conn` CONCURRENTLY with the broadcast that produces
/// it. An over-legacy-cap frame is far bigger than the loopback socket buffer,
/// so a peer that only reads after broadcast() returns would park the send
/// until its (size-scaled) timeout and lose the link.
private func readingConcurrently(
    _ conn: FramedConnection, _ broadcast: @Sendable @escaping () async -> BroadcastResult
) async -> (BroadcastResult, WireMessage?) {
    let box = Locked<WireMessage?>(nil)
    let reader = Task {
        box.set(try? await withTimeout(seconds: 60) { try await conn.receiveMessage() })
    }
    let result = await broadcast()
    _ = await reader.value
    return (result, box.get())
}

// ---- per-link legacy gate: simple clips ---------------------------------

@Test func oversizeTextReachesOnlyTheProtocol12Peer() async throws {
    _ = sharedGateLogURL
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28551, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawPeer(
        port: 28551, token: "tok", nodeID: "old", name: "old-text", minor: 1)
    let modern = try await rawPeer(
        port: 28551, token: "tok", nodeID: "new", name: "new-text", minor: 2)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let big = overLegacyCapText()
    let (result, got) = await readingConcurrently(modern) { await a.broadcast(.text(big)) }

    #expect(result.delivered.count == 1)
    #expect(result.delivered.first?.peerName == "new-text")
    #expect(result.sizeSkipped == ["old-text"])
    // Exact log line — the wording is part of the cross-implementation contract.
    #expect(sharedLogText().contains(
        "clip too large for 'old-text' (peer protocol < 1.2); skipping"))
    // The skipped link is NOT dropped and NOT closed.
    #expect(await a.activeLinkCount() == 2)
    #expect(await a.hasLink(nodeID: "old"))
    // The 1.2 peer really receives the over-legacy-cap frame.
    #expect(got?.kind == "text")
    #expect((got?.content?.count ?? 0) == Wire.legacyMaxPayload + 1024)
    await a.shutdown()
}

@Test func minorZeroPeerIsAlsoGatedOnSimpleClips() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28552, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let ancient = try await rawPeer(
        port: 28552, token: "tok", nodeID: "anc", name: "ancient", minor: 0)
    defer { ancient.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 1 })

    let result = await a.broadcast(.text(overLegacyCapText()))
    #expect(result.delivered.isEmpty)
    #expect(result.sizeSkipped == ["ancient"])
    #expect(await a.activeLinkCount() == 1)     // link kept
    await a.shutdown()
}

@Test func underTheLegacyCapEveryoneGetsTheClip() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28553, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawPeer(port: 28553, token: "tok", nodeID: "o", name: "old", minor: 0)
    let modern = try await rawPeer(port: 28553, token: "tok", nodeID: "n", name: "new", minor: 2)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let result = await a.broadcast(.text("hello"))
    #expect(result.delivered.count == 2)
    #expect(result.sizeSkipped.isEmpty)
    await a.shutdown()
}

// ---- encode-once per broadcast ------------------------------------------

// A single encode per payload variant means a single `ts` for the whole
// fan-out; the pre-fix code built one WireMessage (and one Date()) PER LINK, so
// two peers could never observe the identical timestamp.
@Test func onePayloadVariantIsEncodedOncePerBroadcast() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28554, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let p1 = try await rawPeer(port: 28554, token: "tok", nodeID: "n1", name: "p1", minor: 2)
    let p2 = try await rawPeer(port: 28554, token: "tok", nodeID: "n2", name: "p2", minor: 2)
    defer { p1.cancel(); p2.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    _ = await a.broadcast(.text("shared"))
    let g1 = try await withTimeout(seconds: 5) { try await p1.receiveMessage() }
    let g2 = try await withTimeout(seconds: 5) { try await p2.receiveMessage() }
    #expect(g1?.content == "shared" && g2?.content == "shared")
    #expect(g1?.ts != nil)
    #expect(g1?.ts == g2?.ts)     // same encoded frame handed to both links
    await a.shutdown()
}

// ---- per-link legacy gate: files variants -------------------------------

// The minor-0 link takes the first-file "file" fallback; when THAT variant is
// over the legacy cap it is the one gated, while the minor-2 link still gets
// the full "files" frame.
@Test func firstFileFallbackVariantIsGatedForAMinorZeroPeer() async throws {
    _ = sharedGateLogURL
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28555, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawPeer(
        port: 28555, token: "tok", nodeID: "o", name: "old-files", minor: 0)
    let modern = try await rawPeer(
        port: 28555, token: "tok", nodeID: "n", name: "new-files", minor: 2)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    // The FIRST file alone exceeds the legacy cap once base64'd (13 MB -> ~17.3 MB).
    let files: [(name: String, data: Data, relPath: String?)] = [
        (name: "big.bin", data: Data(count: 13_000_000), relPath: nil),
        (name: "small.txt", data: Data("hi".utf8), relPath: nil),
    ]
    let (result, got) = await readingConcurrently(modern) { await a.broadcast(.files(files)) }

    #expect(result.delivered.count == 1)
    #expect(result.delivered.first?.peerName == "new-files")
    #expect(result.sizeSkipped == ["old-files"])
    // Nothing reached the old peer, so no first-file-fallback toast either.
    #expect(result.maxDropped == 0)
    #expect(await a.activeLinkCount() == 2)
    #expect(sharedLogText().contains(
        "clip too large for 'old-files' (peer protocol < 1.2); skipping"))
    #expect(got?.kind == "files")
    #expect(got?.files?.count == 2)
    await a.shutdown()
}

@Test func minorOnePeerIsGatedOnAnOversizeFilesClip() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28556, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    // minor 1 takes kind:"files" but still enforces the 16 MiB receive cap.
    let mid = try await rawPeer(port: 28556, token: "tok", nodeID: "m", name: "mid", minor: 1)
    let modern = try await rawPeer(port: 28556, token: "tok", nodeID: "n", name: "new", minor: 2)
    defer { mid.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let files: [(name: String, data: Data, relPath: String?)] = [
        (name: "big.bin", data: Data(count: 13_000_000), relPath: nil),
        (name: "small.txt", data: Data("hi".utf8), relPath: nil),
    ]
    let (result, got) = await readingConcurrently(modern) { await a.broadcast(.files(files)) }
    #expect(result.delivered.count == 1)
    #expect(result.delivered.first?.peerName == "new")
    #expect(result.sizeSkipped == ["mid"])
    #expect(got?.kind == "files")
    #expect(await a.activeLinkCount() == 2)
    await a.shutdown()
}

@Test func smallFilesClipStillFansOutWithMinorGating() async throws {
    let clips = Locked<[(ClipPayload, String)]>([]); let events = Locked<[DaemonEvent]>([])
    let a = await makeManager(token: "tok", port: 28557, name: "a", clips: clips, events: events)
    let serve = Task { try await a.serve() }; defer { serve.cancel() }
    #expect(await waitUntil { await a.isServing })

    let old = try await rawPeer(port: 28557, token: "tok", nodeID: "o", name: "old", minor: 0)
    let modern = try await rawPeer(port: 28557, token: "tok", nodeID: "n", name: "new", minor: 2)
    defer { old.cancel(); modern.cancel() }
    #expect(await waitUntil { await a.activeLinkCount() == 2 })

    let files: [(name: String, data: Data, relPath: String?)] = [
        (name: "a.txt", data: Data("one".utf8), relPath: nil),
        (name: "b.txt", data: Data("two".utf8), relPath: nil),
        (name: "c.txt", data: Data("three".utf8), relPath: nil),
    ]
    let result = await a.broadcast(.files(files))
    #expect(result.delivered.count == 2)
    #expect(result.sizeSkipped.isEmpty)
    #expect(result.maxDropped == 2)          // the minor-0 peer took only the first file
    let gOld = try await withTimeout(seconds: 5) { try await old.receiveMessage() }
    #expect(gOld?.kind == "file" && gOld?.name == "a.txt")
    let gNew = try await withTimeout(seconds: 5) { try await modern.receiveMessage() }
    #expect(gNew?.kind == "files" && gNew?.files?.count == 3)
    await a.shutdown()
}

// ---- aggregated skip toast ----------------------------------------------

@Test func sizeSkipMessageIsAtMostOnePerClip() {
    #expect(sizeSkipMessage([]) == nil)
    #expect(sizeSkipMessage(["MacBook"])
        == "clip not sent to MacBook (too large for its AnyClip version)")
    #expect(sizeSkipMessage(["MacBook", "PC", "NUC"])
        == "clip not sent to 3 peer(s) (too large for their AnyClip version)")
}

// ---- receive guard boundary over a real socket --------------------------

@Test func recvRejectsAFrameHeaderOverTheNewCap() async throws {
    let port: UInt16 = 28558
    let inbound = Locked<FramedConnection?>(nil)
    let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    listener.newConnectionHandler = { conn in
        conn.start(queue: .global())
        inbound.set(FramedConnection(connection: conn))
    }
    listener.start(queue: .global())
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }

    // A header claiming one byte more than the cap, plus a tiny body: the guard
    // must reject on the LENGTH alone, without reading the body.
    let n = UInt32(Wire.maxPayload + 1)
    var raw = Data([
        UInt8((n >> 24) & 0xFF), UInt8((n >> 16) & 0xFF),
        UInt8((n >> 8) & 0xFF), UInt8(n & 0xFF),
    ])
    raw.append(Data(#"{"type":"ping"}"#.utf8))
    try await client.sendFrame(EncodedFrame(bytes: raw, bodyCount: raw.count))

    var server: FramedConnection?
    for _ in 0..<200 {
        if let s = inbound.get() { server = s; break }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    let got = try await withTimeout(seconds: 5) { try await server!.receiveMessage() }
    #expect(got == nil)      // invalid frame length -> end of session
    server?.cancel()
}
