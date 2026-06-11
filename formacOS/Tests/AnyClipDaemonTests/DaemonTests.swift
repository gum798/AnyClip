import Testing
import Foundation
@testable import AnyClipDaemon
@testable import AnyClipCore

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-daemon-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func syncCoordinatorSuppressesEcho() async {
    let c = SyncCoordinator()
    await c.markReceived(kind: "text", hash: "h1")
    #expect(!(await c.shouldSend(kind: "text", hash: "h1")))
    #expect(await c.shouldSend(kind: "text", hash: "h2"))
    #expect(await c.shouldSend(kind: "image", hash: "h1"))
}

@Test func clearDirectoryFilesRemovesFilesKeepsSubdirs() throws {
    let dir = tempDir()
    try Data("x".utf8).write(to: dir.appendingPathComponent("a.txt"))
    try FileManager.default.createDirectory(
        at: dir.appendingPathComponent("sub"), withIntermediateDirectories: true)
    clearDirectoryFiles(dir)
    let remaining = try FileManager.default.contentsOfDirectory(atPath: dir.path)
    #expect(remaining == ["sub"])
}

@Test func daemonStartsAndShutsDownCleanly() async throws {
    // Full assembly on a non-default port + isolated state dir, no peers.
    // Verifies: pid file written, listener up, cancellation cleans up.
    let stateDir = tempDir()
    let config = DaemonConfig(
        token: "test-token", port: 28481, name: "daemon-test",
        pollInterval: 0.1, notify: false)
    let daemon = Daemon(
        config: config, appVersion: "0.0.0-test", stateDir: stateDir,
        notifier: { _, _ in }, onFatal: { _ in })

    let runTask = Task { await daemon.runForever() }
    // Wait for the pid file to appear.
    let pidFile = stateDir.appendingPathComponent("anyclip.pid")
    var appeared = false
    for _ in 0..<100 {
        if FileManager.default.fileExists(atPath: pidFile.path) { appeared = true; break }
        try await Task.sleep(nanoseconds: 50_000_000)
    }
    #expect(appeared)

    runTask.cancel()
    _ = await runTask.value
    // PID file released on graceful shutdown.
    #expect(!FileManager.default.fileExists(atPath: pidFile.path))
}
