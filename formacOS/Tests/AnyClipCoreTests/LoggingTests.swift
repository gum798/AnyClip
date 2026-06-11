import Testing
import Foundation
@testable import AnyClipCore

private func tempLogURL() -> URL {
    let dir = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-log-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
    return dir.appendingPathComponent("anyclip.log")
}

@Test func writesFormattedLines() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false)
    log.info("hello world")
    log.flushForTesting()
    let content = try String(contentsOf: url, encoding: .utf8)
    #expect(content.contains(" INFO hello world\n"))
    // "YYYY-MM-DD HH:MM:SS,mmm LEVEL msg" — same shape as Python logging.
    let prefix = content.prefix(23) // "2026-06-11 10:00:00,123"
    #expect(prefix.count == 23)
    #expect(prefix[prefix.index(prefix.startIndex, offsetBy: 4)] == "-")
    #expect(prefix[prefix.index(prefix.startIndex, offsetBy: 19)] == ",")
}

@Test func rotatesAtMaxBytes() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false, maxBytes: 200, backupCount: 3)
    for i in 0..<30 { log.info("line \(i) padding padding padding") }
    log.flushForTesting()
    let dir = url.deletingLastPathComponent()
    let names = try FileManager.default.contentsOfDirectory(atPath: dir.path)
    #expect(names.contains("anyclip.log"))
    #expect(names.contains("anyclip.log.1"))
    // never more than backupCount backups
    #expect(!names.contains("anyclip.log.4"))
    let mainSize = try FileManager.default.attributesOfItem(atPath: url.path)[.size] as! Int
    #expect(mainSize <= 300) // freshly rotated file stays small
}

@Test func debugIsAlwaysInFileLog() throws {
    let url = tempLogURL()
    let log = AnyLog()
    log.configure(fileURL: url, verbose: false)
    log.debug("dbg-marker")
    log.flushForTesting()
    let content = try String(contentsOf: url, encoding: .utf8)
    #expect(content.contains("DEBUG dbg-marker"))
}
