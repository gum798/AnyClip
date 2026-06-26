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

// Regression for the Mac outbound wedge: a send that parks on a closed TCP
// window (peer not reading / half-open) must not block the caller forever. The
// listener accepts but NEVER reads, so a large send fills the socket buffers
// and parks with its completion undelivered — the exact stall that froze the
// clipboard poll loop and the heartbeat self-heal. With a short sendTimeout,
// sendFrame must throw TimeoutError on its own and cancel the connection.
//
// The send runs as an unstructured Task we never await (a parked, no-timeout
// sendFrame would otherwise hang the test); we poll a shared box for the
// outcome. Before the fix it stays nil → the assert fails fast (no hang);
// after the fix it flips to "timeout" within the budget.
@Test func sendFrameBailsWhenSendStalls() async throws {
    let port: UInt16 = 28468
    let listener = try startLoopbackListener(port: port) { conn in
        conn.start(queue: .global()) // accept, then never receive()
    }
    defer { listener.cancel() }

    let client = FramedConnection.outbound(
        to: .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!))
    try await client.start()
    client.sendTimeout = 0.3
    defer { client.cancel() }

    // 15 MiB (just under the 16 MiB frame cap): must exceed the runner's
    // combined loopback send+recv buffer so the send parks on the closed TCP
    // window. On this machine the send wedges after ~1 MiB, so this is a wide
    // margin; sized near the cap to stay robust against CI buffer autotuning.
    let big = String(repeating: "x", count: 15 * 1024 * 1024)
    let outcome = Locked<String?>(nil)
    let sendTask = Task {
        do { try await client.sendFrame(.clipText(big, ts: 0)); outcome.set("returned") }
        catch is TimeoutError { outcome.set("timeout") }
        catch { outcome.set("other") }
    }
    for _ in 0..<400 { // up to ~4 s, past the 0.3 s budget
        if outcome.get() != nil { break }
        try await Task.sleep(nanoseconds: 10_000_000)
    }
    sendTask.cancel()
    #expect(outcome.get() == "timeout") // before the fix: still nil
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
