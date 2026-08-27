import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

private func scriptsDir() -> URL {
    // <pkg>/Tests/AnyClipDaemonTests/InteropTests.swift -> <pkg>/Scripts
    URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent()   // AnyClipDaemonTests
        .deletingLastPathComponent()   // Tests
        .deletingLastPathComponent()   // formacOS
        .appendingPathComponent("Scripts")
}

@Test func interopWithPythonFakePeer() async throws {
    let port: UInt16 = 28491
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", "interop-token",
        "--out", outFile.path,
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    defer { if process.isRunning { process.terminate() } }

    // Wait for READY with a deadline to avoid flakiness from availableData
    // returning before READY is written.
    var readyReceived = false
    let readyDeadline = Date().addingTimeInterval(10)
    var accumulated = Data()
    while Date() < readyDeadline {
        let chunk = stdout.fileHandleForReading.availableData
        if !chunk.isEmpty { accumulated.append(chunk) }
        if let s = String(data: accumulated, encoding: .utf8), s.contains("READY") {
            readyReceived = true
            break
        }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    try #require(readyReceived)

    let clips = Locked<[ClipPayload]>([])
    let events = Locked<[DaemonEvent]>([])
    let peerNameBox = Locked<String?>(nil)
    // Tight per-link ping so the automatic keepalive surfaces a ping frame to
    // the fake peer within the test window; the fake peer pongs each ping so
    // the large deadFactor never trips the staleness dropper.
    let manager = LinkManager(
        config: LinkManager.LinkConfig(
            token: "interop-token", port: 28492, name: "swift-interop",
            appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased(),
        pingInterval: 0.5, pingDeadFactor: 20)
    await manager.setHandlers(
        onClip: { payload, peer in
            clips.set(clips.get() + [payload]); peerNameBox.set(peer)
        },
        emit: { events.set(events.get() + [$0]) })

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    // Link comes up with the Python peer after a routed dial.
    let outcome = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
        label: "127.0.0.1:\(port)")
    #expect(outcome == .routed)
    #expect(await waitUntil(5) { await manager.activeLinkCount() == 1 })

    // Python -> Swift text clip arrives, tagged with the peer's name.
    #expect(await waitUntil(5) {
        clips.get().contains {
            if case .text(let s) = $0 { return s == "hello-from-python" }
            return false
        }
    })
    #expect(peerNameBox.get() == "fake-peer")

    // Swift -> Python: text + image + file (+ automatic ping from the loop).
    _ = await manager.broadcast(.text("hello-from-swift"))
    _ = await manager.broadcast(.image(Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])))
    _ = await manager.broadcast(.file(name: "노트.txt", data: Data("file-content".utf8)))

    #expect(await waitUntil(5) {
        guard let lines = try? String(contentsOf: outFile, encoding: .utf8) else {
            return false
        }
        return lines.contains("hello-from-swift")
            && lines.contains("\"kind\": \"file\"")
            && lines.contains("노트.txt")
            && lines.contains("\"kind\": \"image\"")
            && lines.contains("\"type\": \"ping\"")
    })

    // Swift -> a legacy (minor-0) Python peer: a two-file kind:"files" copy is
    // downgraded per-link to its first file on the wire (dropped == 1). The
    // second file never reaches the peer; the first does.
    let mf1 = (name: "노트-multi.txt", data: Data("files body one".utf8), relPath: String?.none)
    let mf2 = (name: "(E&S) plan.txt", data: Data("files body two".utf8), relPath: String?.none)
    let filesResult = await manager.broadcast(.files([mf1, mf2]))
    #expect(filesResult.maxDropped == 1)
    #expect(await waitUntil(5) {
        guard let lines = try? String(contentsOf: outFile, encoding: .utf8) else { return false }
        return lines.contains("노트-multi.txt")     // first file, sent as kind:"file"
            && !lines.contains("(E&S) plan.txt")    // second file dropped for the old peer
    })

    // The hello we sent must satisfy Python's field expectations.
    let outText = try String(contentsOf: outFile, encoding: .utf8)
    let helloLine = outText.split(separator: "\n").first { $0.contains("\"event\": \"hello\"") }
    let hello = try #require(helloLine)
    #expect(hello.contains("\"version\": 1"))
    #expect(hello.contains("\"protocol_major\": 1"))

    await manager.shutdown()
}

@Test func interopReceivesMultipleFilesFromFakePeer() async throws {
    let port: UInt16 = 28493
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")

    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", "interop-token",
        "--out", outFile.path, "--send-files",
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    defer { if process.isRunning { process.terminate() } }

    var readyReceived = false
    let readyDeadline = Date().addingTimeInterval(10)
    var accumulated = Data()
    while Date() < readyDeadline {
        let chunk = stdout.fileHandleForReading.availableData
        if !chunk.isEmpty { accumulated.append(chunk) }
        if let s = String(data: accumulated, encoding: .utf8), s.contains("READY") {
            readyReceived = true
            break
        }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    try #require(readyReceived)

    let clips = Locked<[ClipPayload]>([])
    let events = Locked<[DaemonEvent]>([])
    let manager = LinkManager(
        config: LinkManager.LinkConfig(
            token: "interop-token", port: 28494, name: "swift-interop",
            appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await manager.setHandlers(
        onClip: { payload, _ in clips.set(clips.get() + [payload]) },
        emit: { events.set(events.get() + [$0]) })

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    let outcome = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
        label: "127.0.0.1:\(port)")
    #expect(outcome == .routed)
    #expect(await waitUntil(5) { await manager.activeLinkCount() == 1 })

    // Python -> Swift: the two-file batch surfaces with intact names, incl. the
    // parens/ampersand name the OLD whitelist would have mangled.
    #expect(await waitUntil(5) {
        clips.get().contains {
            if case .files(let fs) = $0 {
                return fs.count == 2
                    && fs.contains { $0.name == "노트.txt" && $0.data == Data("multi body one".utf8) }
                    && fs.contains { $0.name == "(E&S) plan.txt" && $0.data == Data("multi body two".utf8) }
            }
            return false
        }
    })
    // The received name is denylist-safe end-to-end: sanitize keeps it verbatim.
    #expect(sanitizeFilename("(E&S) plan.txt") == "(E&S) plan.txt")

    // Aggregate recomputed from the decoded bytes matches the CONTRACT formula.
    let expected = aggregateFilesHash([
        sha256Hex(Data("multi body one".utf8)), sha256Hex(Data("multi body two".utf8))])
    #expect(clips.get().contains {
        if case .files(let fs) = $0 {
            return aggregateFilesHash(fs.map { sha256Hex($0.data) }) == expected
        }
        return false
    })

    await manager.shutdown()
}

/// Spawn a wire-compatible fake peer that listens on `port`, handshakes, and
/// auto-sends one text clip; returns the process (for teardown) and its
/// record outfile. Waits for READY on stdout.
private func startFakePeer(port: UInt16, token: String) async throws -> (Process, URL) {
    let outFile = FileManager.default.temporaryDirectory
        .appendingPathComponent("fake-peer-\(UUID().uuidString).jsonl")
    let process = Process()
    process.executableURL = URL(fileURLWithPath: "/usr/bin/env")
    process.arguments = [
        "python3", scriptsDir().appendingPathComponent("fake_peer.py").path,
        "--port", "\(port)", "--token", token, "--out", outFile.path,
    ]
    let stdout = Pipe()
    process.standardOutput = stdout
    try process.run()
    var ready = false
    let deadline = Date().addingTimeInterval(10)
    var accumulated = Data()
    while Date() < deadline {
        let chunk = stdout.fileHandleForReading.availableData
        if !chunk.isEmpty { accumulated.append(chunk) }
        if let s = String(data: accumulated, encoding: .utf8), s.contains("READY") {
            ready = true; break
        }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    guard ready else { throw WireConnectionError.closed }
    return (process, outFile)
}

/// Count of frames the fake peer RECEIVED from us (its "recv" event lines).
private func recvClipCount(_ url: URL) -> Int {
    guard let s = try? String(contentsOf: url, encoding: .utf8) else { return 0 }
    return s.split(separator: "\n").filter { $0.contains("\"event\": \"recv\"") }.count
}

private func fileContains(_ url: URL, _ needle: String) -> Bool {
    (try? String(contentsOf: url, encoding: .utf8))?.contains(needle) ?? false
}

@Test func meshLinksBothPeersAndNeverRelays() async throws {
    let portA: UInt16 = 28495
    let portB: UInt16 = 28496
    let (procA, outA) = try await startFakePeer(port: portA, token: "mesh-token")
    let (procB, outB) = try await startFakePeer(port: portB, token: "mesh-token")
    defer {
        if procA.isRunning { procA.terminate() }
        if procB.isRunning { procB.terminate() }
    }

    let clips = Locked<[(ClipPayload, String)]>([])
    let manager = LinkManager(
        config: LinkManager.LinkConfig(
            token: "mesh-token", port: 28497, name: "swift-mesh", appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await manager.setHandlers(
        onClip: { payload, peer in clips.set(clips.get() + [(payload, peer)]) },
        emit: { _ in })

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    // Dial BOTH peers: full mesh from this node's side.
    _ = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: portA)!), label: "a")
    _ = await manager.tryConnect(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: portB)!), label: "b")
    #expect(await waitUntil(5) { await manager.activeLinkCount() == 2 })

    // Both fake peers auto-send "hello-from-python"; the manager APPLIES both
    // locally (onClip records them). This proves both links carry traffic.
    #expect(await waitUntil(5) {
        clips.get().filter {
            if case .text(let s) = $0.0 { return s == "hello-from-python" }; return false
        }.count == 2
    })

    // Non-relay: the manager received A's (and B's) clip but has NO relay path,
    // so it must forward nothing to the other peer. Give it a bounded window,
    // then assert neither fake peer received ANY clip frame from us. (This is
    // also the echo-suppression-under-mesh guarantee: an applied clip is never
    // rebroadcast.)
    try await Task.sleep(nanoseconds: 1_000_000_000)
    #expect(recvClipCount(outA) == 0)
    #expect(recvClipCount(outB) == 0)

    // The mesh works the other way: one LOCAL clip reaches BOTH peers.
    _ = await manager.broadcast(.text("local-broadcast"))
    #expect(await waitUntil(5) {
        fileContains(outA, "local-broadcast") && fileContains(outB, "local-broadcast")
    })
    // That broadcast is the ONLY frame each peer received — still no relay of
    // the other peer's hello clip.
    #expect(await waitUntil(3) { recvClipCount(outA) == 1 })
    #expect(await waitUntil(3) { recvClipCount(outB) == 1 })

    await manager.shutdown()
}
