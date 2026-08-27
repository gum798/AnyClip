import Testing
import Foundation
@testable import AnyClipDaemon
@testable import AnyClipCore

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-walk-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

private func write(_ dir: URL, _ rel: String, _ body: String) throws {
    let target = rel.split(separator: "/").reduce(dir) { $0.appendingPathComponent(String($1)) }
    try FileManager.default.createDirectory(
        at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data(body.utf8).write(to: target)
}

@Test func walkSortsByRelPathBytesAndPrefixesTheTopName() throws {
    let root = tempDir()
    let top = root.appendingPathComponent("docs", isDirectory: true)
    try write(top, "c.txt", "c")
    try write(top, "a.txt", "a")
    try write(top, "sub/b.txt", "b")
    try write(top, "sub/a.txt", "sa")
    let got = FolderExpander.walk(top)
    #expect(got.map(\.relPath)
        == ["docs/a.txt", "docs/c.txt", "docs/sub/a.txt", "docs/sub/b.txt"])
    #expect(got.map(\.size) == [1, 1, 2, 1])
}

@Test func walkExcludesJunkAndNeverFollowsSymlinks() throws {
    let root = tempDir()
    let top = root.appendingPathComponent("docs", isDirectory: true)
    try write(top, "keep.txt", "k")
    try write(top, ".DS_Store", "junk")
    try write(top, "sub/Thumbs.db", "junk")
    try write(top, "sub/desktop.ini", "junk")
    let outside = root.appendingPathComponent("outside.txt")
    try Data("secret".utf8).write(to: outside)
    try FileManager.default.createSymbolicLink(
        at: top.appendingPathComponent("link.txt"), withDestinationURL: outside)
    try FileManager.default.createSymbolicLink(
        at: top.appendingPathComponent("linkdir"), withDestinationURL: root)
    #expect(FolderExpander.walk(top).map(\.relPath) == ["docs/keep.txt"])
}

@Test func walkDropsEmptyDirectories() throws {
    let root = tempDir()
    let top = root.appendingPathComponent("docs", isDirectory: true)
    try FileManager.default.createDirectory(
        at: top.appendingPathComponent("empty/deeper"), withIntermediateDirectories: true)
    #expect(FolderExpander.walk(top).isEmpty)
    try write(top, "empty/deeper/x.txt", "x")
    #expect(FolderExpander.walk(top).map(\.relPath) == ["docs/empty/deeper/x.txt"])
}

@Test func walkEmitsNFCRelPathsForKoreanNames() throws {
    let root = tempDir()
    let nfd = "결과".decomposedStringWithCanonicalMapping
    let nfc = "결과".precomposedStringWithCanonicalMapping
    let top = root.appendingPathComponent(nfd, isDirectory: true)
    try write(top, nfd + ".txt", "x")
    let got = FolderExpander.walk(top)
    #expect(got.count == 1)
    // Swift's == is canonical, so assert on the actual UTF-8 bytes.
    #expect(Array(got[0].relPath.utf8) == Array((nfc + "/" + nfc + ".txt").utf8))
    #expect(FileManager.default.fileExists(atPath: got[0].url.path))   // read path unchanged
}

/// The walk runs on EVERY poll (noticing an edit deep inside a tree requires
/// it), so a folder past the absolute caps must not be re-scanned in full
/// forever. It bails out one item PAST the cap — exactly what the watcher's
/// admission check needs to reject the folder — and the kept prefix has to be
/// STABLE, or the fingerprint would churn and re-toast every cycle.
/// Mirrors tests/test_folder_walk.py::test_walk_stops_early_once_the_absolute_file_cap_is_blown.
@Test func walkStopsEarlyOnceTheAbsoluteFileCapIsBlown() throws {
    let root = tempDir()
    let top = root.appendingPathComponent("big", isDirectory: true)
    try FileManager.default.createDirectory(at: top, withIntermediateDirectories: true)
    let cap = ClipboardWatcher.maxFilesPerClip
    // Spread over two directories so the early-out has to unwind the recursion.
    for i in 0..<(cap + 20) {
        try write(top, i.isMultiple(of: 2) ? "f\(i).txt" : "sub/f\(i).txt", "x")
    }
    let first = FolderExpander.walk(top)
    #expect(first.count == cap + 1)              // one past the cap, never the whole tree
    #expect(first.map(\.relPath) == FolderExpander.walk(top).map(\.relPath))   // stable prefix
}

/// Same early-out on the byte budget: two sparse files that each fit alone but
/// blow fileBudget together stop the walk at the second one.
@Test func walkStopsEarlyOnceTheByteBudgetIsBlown() throws {
    let root = tempDir()
    let top = root.appendingPathComponent("heavy", isDirectory: true)
    try FileManager.default.createDirectory(at: top, withIntermediateDirectories: true)
    let each = ClipboardWatcher.fileBudget / 2 + 1
    for name in ["a.bin", "b.bin", "c.bin"] {
        let url = top.appendingPathComponent(name)
        FileManager.default.createFile(atPath: url.path, contents: nil)
        let h = try FileHandle(forWritingTo: url)
        try h.truncate(atOffset: UInt64(each))
        try h.close()
    }
    #expect(FolderExpander.walk(top).map(\.relPath) == ["heavy/a.bin", "heavy/b.bin"])
}
