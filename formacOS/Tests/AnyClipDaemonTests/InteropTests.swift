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
    let link = PeerLink(
        config: PeerLink.LinkConfig(
            token: "interop-token", port: 28492, name: "swift-interop",
            appVersion: "0.0.0-test"),
        nodeID: UUID().uuidString.lowercased())
    await link.setHandlers(
        onClip: { clips.set(clips.get() + [$0]) },
        emit: { events.set(events.get() + [$0]) })

    let sessionTask = Task {
        await link.tryConnect(
            to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!),
            label: "127.0.0.1:\(port)")
    }
    defer { sessionTask.cancel() }

    func waitUntil(_ timeout: Double, _ cond: @escaping () async -> Bool) async -> Bool {
        let deadline = monotonicNow() + timeout
        while monotonicNow() < deadline {
            if await cond() { return true }
            try? await Task.sleep(nanoseconds: 50_000_000)
        }
        return await cond()
    }

    // Link comes up with the Python peer's name.
    #expect(await waitUntil(5) { await link.isActive })
    #expect(await link.peerName == "fake-peer")

    // Python -> Swift text clip arrives.
    #expect(await waitUntil(5) {
        clips.get().contains {
            if case .text(let s) = $0 { return s == "hello-from-python" }
            return false
        }
    })

    // Swift -> Python: text + image + file + ping.
    await link.sendClip(.text("hello-from-swift"))
    await link.sendClip(.image(Data([0x89, 0x50, 0x4E, 0x47, 1, 2, 3])))
    await link.sendClip(.file(name: "노트.txt", data: Data("file-content".utf8)))
    await link.sendPing()

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

    // The hello we sent must satisfy Python's field expectations.
    let outText = try String(contentsOf: outFile, encoding: .utf8)
    let helloLine = outText.split(separator: "\n").first { $0.contains("\"event\": \"hello\"") }
    let hello = try #require(helloLine)
    #expect(hello.contains("\"version\": 1"))
    #expect(hello.contains("\"protocol_major\": 1"))

    await link.shutdown()
}
