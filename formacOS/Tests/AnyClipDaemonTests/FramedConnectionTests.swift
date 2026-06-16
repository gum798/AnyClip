import Testing
import Foundation
import Network
@testable import AnyClipDaemon
@testable import AnyClipCore

/// Loopback NWListener that hands its first inbound connection to the test.
private func startLoopbackListener(
    port: UInt16, onConnection: @escaping (NWConnection) -> Void
) throws -> NWListener {
    let listener = try NWListener(using: .tcp, on: NWEndpoint.Port(rawValue: port)!)
    listener.newConnectionHandler = onConnection
    listener.start(queue: .global())
    return listener
}

@Test func sendAndReceiveFrameOverLoopback() async throws {
    let port: UInt16 = 28461
    let inbound = Locked<FramedConnection?>(nil)
    let listener = try startLoopbackListener(port: port) { conn in
        let framed = FramedConnection(connection: conn)
        conn.start(queue: .global())
        inbound.set(framed)
    }
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }

    try await client.sendFrame(.clipText("ping-pong", ts: 1))
    // Wait for the listener side to appear, then read the frame there.
    var server: FramedConnection?
    for _ in 0..<100 {
        if let s = inbound.get() { server = s; break }
        try await Task.sleep(nanoseconds: 20_000_000)
    }
    let received = try await server!.receiveMessage()
    #expect(received?.type == "clip")
    #expect(received?.content == "ping-pong")
    server?.cancel()
}

@Test func eofSurfacesAsConnectionClosed() async throws {
    let port: UInt16 = 28462
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global())
        // Close immediately after accept.
        conn.cancel()
    }
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }
    await #expect(throws: (any Error).self) {
        _ = try await client.receiveMessage()
    }
}

@Test func withTimeoutThrowsOnSlowOperation() async throws {
    await #expect(throws: TimeoutError.self) {
        try await withTimeout(seconds: 0.05) {
            try await Task.sleep(nanoseconds: 2_000_000_000)
        }
    }
}

@Test func withTimeoutPassesThroughFastResult() async throws {
    let v = try await withTimeout(seconds: 1.0) { 42 }
    #expect(v == 42)
}

@Test func remoteIPIsCapturedOnReady() async throws {
    let port: UInt16 = 28463
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global())
    }
    defer { listener.cancel() }
    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }
    #expect(client.remoteIP == "127.0.0.1")
}

// Regression: a parked receive (peer connected, sending nothing) must wake
// up when its Task is cancelled. Without cancellation wired into
// NWConnection.receive, the continuation never resumes, so a structured
// task-group shutdown — i.e. app quit WHILE LINKED — deadlocks and the app
// never terminates.
@Test func receiveMessageHonorsCancellation() async throws {
    let port: UInt16 = 28464
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global()) // accept, then send nothing
    }
    defer { listener.cancel() }
    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    defer { client.cancel() }

    let receiveTask = Task { try await client.receiveMessage() }
    try await Task.sleep(nanoseconds: 200_000_000) // let it park in receive
    receiveTask.cancel()

    // Race the task's completion against a 2 s deadline: true = it woke up
    // and finished (cancellation honored), false = it hung past the deadline.
    let finished = await withTaskGroup(of: Bool.self) { group in
        group.addTask { _ = try? await receiveTask.value; return true }
        group.addTask {
            try? await Task.sleep(nanoseconds: 2_000_000_000); return false
        }
        let first = await group.next()!
        group.cancelAll()
        return first
    }
    #expect(finished)
}
