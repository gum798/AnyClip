import Testing
import Foundation
@testable import AnyClipDaemon

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-pid-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func prepareWritesOwnPidAndPort() throws {
    let dir = tempDir()
    try PidLock.prepare(port: 58161, dir: dir)
    let content = try String(contentsOf: dir.appendingPathComponent("anyclip.pid"),
                             encoding: .utf8)
    #expect(content == "\(getpid()) 58161\n")
    PidLock.release(dir: dir)
    #expect(!FileManager.default.fileExists(
        atPath: dir.appendingPathComponent("anyclip.pid").path))
}

@Test func staleDeadPidIsOverwritten() throws {
    let dir = tempDir()
    // Use an impossible pid so processAlive() is false and no kill happens.
    try "999999 58161\n".write(to: dir.appendingPathComponent("anyclip.pid"),
                               atomically: true, encoding: .utf8)
    try PidLock.prepare(port: 58161, dir: dir)
    let content = try String(contentsOf: dir.appendingPathComponent("anyclip.pid"),
                             encoding: .utf8)
    #expect(content.hasPrefix("\(getpid()) "))
    PidLock.release(dir: dir)
}

@Test func releaseLeavesForeignPidFileAlone() throws {
    let dir = tempDir()
    try "999999 58161\n".write(to: dir.appendingPathComponent("anyclip.pid"),
                               atomically: true, encoding: .utf8)
    PidLock.release(dir: dir)
    #expect(FileManager.default.fileExists(
        atPath: dir.appendingPathComponent("anyclip.pid").path))
}

@Test func isAnyclipPidMatchesCaseInsensitively() {
    // Pure matcher test.
    #expect(PidLock.argsLookLikeAnyclip("/Applications/AnyClip.app/Contents/MacOS/AnyClip"))
    #expect(PidLock.argsLookLikeAnyclip("python3 /Users/x/AnyClip/anyclip.py --headless"))
    #expect(!PidLock.argsLookLikeAnyclip("/usr/bin/nc -l 58161"))
}
