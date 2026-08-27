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

@Test func clearDirectoryContentsRemovesFilesAndWholeSubtrees() throws {
    let fm = FileManager.default
    let dir = tempDir()
    try Data("x".utf8).write(to: dir.appendingPathComponent("a.txt"))
    // received/ holds rebuilt TREES since protocol 1.3, so a subdirectory is
    // inbound clipboard data like any other and goes with the rest — leaving it
    // grew disk use without bound across restarts AND ratcheted the next
    // colliding top to "docs-2", "docs-3", … forever.
    let deep = dir.appendingPathComponent("docs/sub")
    try fm.createDirectory(at: deep, withIntermediateDirectories: true)
    try Data("y".utf8).write(to: deep.appendingPathComponent("b.txt"))

    clearDirectoryContents(dir)

    #expect(try fm.contentsOfDirectory(atPath: dir.path).isEmpty)
    #expect(fm.fileExists(atPath: dir.path))   // the directory itself survives
}

@Test func clearDirectoryContentsUnlinksASymlinkToADirectoryWithoutDescending() throws {
    let fm = FileManager.default
    let dir = tempDir()
    let outside = tempDir()
    let keeper = outside.appendingPathComponent("keep.txt")
    try Data("kept".utf8).write(to: keeper)
    let link = dir.appendingPathComponent("link-to-dir")
    try fm.createSymbolicLink(at: link, withDestinationURL: outside)

    clearDirectoryContents(dir)

    // The LINK is gone; what it pointed at is untouched. A probe that resolves
    // the link (URLResourceKey.isDirectoryKey does) would have called this a
    // directory and emptied the target instead.
    #expect(try fm.contentsOfDirectory(atPath: dir.path).isEmpty)
    #expect(fm.fileExists(atPath: outside.path))
    #expect(try Data(contentsOf: keeper) == Data("kept".utf8))
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

@Test func downgradeForPeerKeepsFilesForModernPeer() {
    let payload = ClipPayload.files([(name: "a", data: Data([1]), relPath: nil), (name: "b", data: Data([2]), relPath: nil)])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 1)
    #expect(dropped == 0)
    if case .files(let fs)? = out { #expect(fs.count == 2) } else { Issue.record("expected .files") }
}

@Test func downgradeForPeerDropsToFirstFileForOldPeer() {
    let payload = ClipPayload.files([(name: "a", data: Data([1]), relPath: nil), (name: "b", data: Data([2]), relPath: nil)])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
    #expect(dropped == 1)                                     // one file left behind
    if case .file(let name, let data)? = out {
        #expect(name == "a"); #expect(data == Data([1]))     // first file, legacy kind:"file"
    } else { Issue.record("expected .file") }
}

@Test func downgradePassesNonFilesPayloadsThrough() {
    let (out, dropped) = downgradeForPeer(.text("hi"), peerMinor: 0)
    #expect(dropped == 0)
    if case .text(let s)? = out { #expect(s == "hi") } else { Issue.record("expected .text") }
}

@Test func downgradeExcludesFolderEntriesForAMinorZeroPeer() {
    let payload = ClipPayload.files([
        (name: "a.txt", data: Data([1]), relPath: "docs/a.txt"),
        (name: "loose.txt", data: Data([2]), relPath: nil),
    ])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
    #expect(dropped == 1)
    if case .file(let name, let data)? = out {
        #expect(name == "loose.txt")            // the first LOOSE file, not the tree entry
        #expect(data == Data([2]))
    } else { Issue.record("expected the first loose file, got \(String(describing: out))") }
}

@Test func folderOnlyClipSendsNothingToAMinorZeroPeer() {
    let payload = ClipPayload.files([
        (name: "a.txt", data: Data([1]), relPath: "docs/a.txt"),
        (name: "b.txt", data: Data([2]), relPath: "docs/b.txt"),
    ])
    let (out, dropped) = downgradeForPeer(payload, peerMinor: 0)
    #expect(out == nil)         // nothing a kind:"file" frame could carry
    #expect(dropped == 0)       // and therefore no "files not synced" toast
}
