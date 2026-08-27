import Testing
import Foundation
import AppKit
@testable import AnyClipDaemon
@testable import AnyClipCore

private func privatePasteboard() -> NSPasteboard {
    NSPasteboard(name: NSPasteboard.Name("anyclip-test-\(UUID().uuidString)"))
}

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-watch-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@MainActor
private func makeWatcher(
    _ pb: NSPasteboard, received: URL,
    changes: Locked<[ClipPayload]>, skipped: Locked<[String]>
) -> ClipboardWatcher {
    ClipboardWatcher(
        pasteboard: pb, pollInterval: 0.05, receivedDir: received,
        callbacks: ClipboardWatcher.Callbacks(
            onChange: { changes.set(changes.get() + [$0]) },
            onFileSkipped: { skipped.set(skipped.get() + [$0]) }))
}

@Test @MainActor func textChangeFiresOnChange() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    pb.clearContents()
    pb.setString("fresh text", forType: .string)
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .text(let s) = got[0] { #expect(s == "fresh text") } else { Issue.record("not text") }
}

@Test @MainActor func unchangedChangeCountSkipsAllReads() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    await watcher.pollOnceForTesting()
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func preexistingClipboardContentIsBaselinedNotSent() async throws {
    let pb = privatePasteboard()
    pb.clearContents()
    pb.setString("already there", forType: .string)
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func emptyTextIsNotPropagated() async throws {
    let pb = privatePasteboard()
    pb.clearContents()
    pb.setString("seed", forType: .string)
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    pb.clearContents()
    pb.setString("", forType: .string)
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func updateLocalTextDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    watcher.updateLocalText("from peer")
    #expect(pb.string(forType: .string) == "from peer")
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func imageChangeFiresOnceThenCooldownAbsorbs() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    // 1x1 red PNG
    let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: 1, pixelsHigh: 1,
        bitsPerSample: 8, samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
        colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0)!
    rep.setColor(.red, atX: 0, y: 0)
    let png1 = rep.representation(using: .png, properties: [:])!
    pb.clearContents()
    pb.setData(png1, forType: .png)
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
    // Different bytes within the cooldown window: absorbed silently.
    rep.setColor(.blue, atX: 0, y: 0)
    let png2 = rep.representation(using: .png, properties: [:])!
    pb.clearContents()
    pb.setData(png2, forType: .png)
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
}

/// Materialize `rel` under `dir` (creating intermediate dirs) with `body`.
private func writeFile(_ dir: URL, _ rel: String, _ body: String) throws -> URL {
    let target = rel.split(separator: "/").reduce(dir) { $0.appendingPathComponent(String($1)) }
    try FileManager.default.createDirectory(
        at: target.deletingLastPathComponent(), withIntermediateDirectories: true)
    try Data(body.utf8).write(to: target)
    return target
}

@Test @MainActor func folderOnClipboardExpandsIntoFilesWithPaths() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let root = tempDir()
    let folder = root.appendingPathComponent("docs", isDirectory: true)
    _ = try writeFile(folder, "a.txt", "one")
    _ = try writeFile(folder, "sub/b.txt", "two")
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] {
        #expect(fs.map(\.name) == ["a.txt", "b.txt"])
        #expect(fs.map(\.relPath) == ["docs/a.txt", "docs/sub/b.txt"])
        #expect(fs[0].data == Data("one".utf8))
    } else { Issue.record("expected .files, got \(got)") }
    #expect(skipped.get().isEmpty)
    // Same copy is never re-detected (fingerprints cover the expanded tree).
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
}

@Test @MainActor func emptyFolderToastsAndSendsNothing() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let folder = tempDir()   // a real, empty directory
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
    #expect(skipped.get() == ["folder is empty; nothing to sync"])
    await watcher.pollOnceForTesting()
    #expect(skipped.get().count == 1)          // not re-detected
}

@Test @MainActor func smallFileOnClipboardIsSent() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let file = tempDir().appendingPathComponent("note.txt")
    try Data("file-body".utf8).write(to: file)
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, let data) = got[0] {
        #expect(name == "note.txt")
        #expect(data == Data("file-body".utf8))
    } else { Issue.record("not a file payload") }
}

/// A file of exactly `size` bytes without writing `size` bytes, so the real
/// ~49 MB budget boundary can be exercised cheaply.
private func sparseFile(_ url: URL, size: Int) throws -> URL {
    FileManager.default.createFile(atPath: url.path, contents: nil)
    let h = try FileHandle(forWritingTo: url)
    try h.truncate(atOffset: UInt64(size))
    try h.close()
    return url
}

@Test func fileBudgetKeepsItsFormulaAgainstTheNewCap() {
    #expect(ClipboardWatcher.fileBudget
        == Int(Double(Wire.maxPayload - 256 * 1024) * 0.74))
    #expect(ClipboardWatcher.fileBudget == 49_466_572)   // in lockstep with Python
}

@Test @MainActor func singleFileAtTheBudgetIsAccepted() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let file = try sparseFile(
        tempDir().appendingPathComponent("at-budget.bin"),
        size: ClipboardWatcher.fileBudget)
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, let data) = got.first {
        #expect(name == "at-budget.bin")
        #expect(data.count == ClipboardWatcher.fileBudget)
    } else { Issue.record("expected a single-file payload, got \(got)") }
    #expect(skipped.get().isEmpty)
}

@Test @MainActor func oversizedFileIsSkipped() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    // One byte past the greedy budget (never read: the size check comes first).
    let file = try sparseFile(
        tempDir().appendingPathComponent("big.bin"),
        size: ClipboardWatcher.fileBudget + 1)
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
    #expect(skipped.get() == ["1 file(s) skipped (too large to sync)"])
}

@Test @MainActor func updateLocalFileWritesToReceivedDirAndDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let received = tempDir()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
    let ok = watcher.updateLocalFile(name: "in:va/lid.txt", data: Data("x".utf8))
    #expect(ok)
    // basename rule: os.path.basename("in:va/lid.txt") == "lid.txt", so the
    // ":" never reaches the sanitized name.
    let target = received.appendingPathComponent("lid.txt")
    #expect(FileManager.default.fileExists(atPath: target.path))
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}

@Test @MainActor func twoFilesOnClipboardEmitsFilesPayload() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents()
    pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] {
        #expect(fs.count == 2)
        #expect(fs.contains { $0.name == "a.txt" && $0.data == Data("one".utf8) })
        #expect(fs.contains { $0.name == "b.txt" && $0.data == Data("two".utf8) })
    } else { Issue.record("expected .files payload") }
}

@Test @MainActor func sameFileSelectionDetectedOnlyOnce() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
    // Re-copy the identical selection: changeCount ticks, but the fingerprint
    // list is unchanged, so nothing re-emits.
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().count == 1)
}

@Test @MainActor func folderMixedWithLooseFilesKeepsSelectionOrder() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let folder = dir.appendingPathComponent("sub", isDirectory: true)
    _ = try writeFile(folder, "inner.txt", "i")
    let loose = try writeFile(dir, "a.txt", "one")
    pb.clearContents()
    pb.writeObjects([folder as NSURL, loose as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] {
        // Selection order: the folder's entries first, then the loose file.
        #expect(fs.map(\.relPath) == ["sub/inner.txt", nil])
        #expect(fs.map(\.name) == ["inner.txt", "a.txt"])
    } else { Issue.record("expected .files, got \(got)") }
    #expect(skipped.get().isEmpty)
}

@Test @MainActor func twoEmptyFoldersEmitOneEmptyToastAndTheLooseFileStillSyncs() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let d1 = dir.appendingPathComponent("one", isDirectory: true)
    let d2 = dir.appendingPathComponent("two", isDirectory: true)
    try FileManager.default.createDirectory(at: d1, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: d2, withIntermediateDirectories: true)
    let keep = try writeFile(dir, "keep.txt", "k")
    pb.clearContents()
    pb.writeObjects([d1 as NSURL, d2 as NSURL, keep as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "keep.txt") }
    else { Issue.record("expected single-file payload, got \(got)") }
    #expect(skipped.get() == ["folder is empty; nothing to sync"])
}

@Test @MainActor func oversizeFolderIsAllOrNothingWithItsOwnToast() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let folder = dir.appendingPathComponent("huge", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    // Two sparse halves: each fits alone, the TOTAL does not -> the WHOLE
    // folder is skipped before a single byte is read (no partial trees).
    let each = ClipboardWatcher.fileBudget / 2 + 1
    _ = try sparseFile(folder.appendingPathComponent("p1.bin"), size: each)
    _ = try sparseFile(folder.appendingPathComponent("p2.bin"), size: each)
    let loose = try writeFile(dir, "small.txt", "s")
    pb.clearContents()
    pb.writeObjects([folder as NSURL, loose as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "small.txt") }
    else { Issue.record("expected the loose file only, got \(got)") }
    #expect(skipped.get() == ["folder too large to sync: huge"])
}

/// All-or-nothing is measured against what the selection has LEFT, not against
/// an empty budget: a folder that would fit on its own is still rejected whole
/// once an earlier item in the selection has eaten the room. Byte-budget arm,
/// with a nonzero `running`.
@Test @MainActor func folderThatFitsAloneIsRejectedOnceAPriorFileAteTheBudget() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let half = ClipboardWatcher.fileBudget / 2 + 1
    let loose = try sparseFile(dir.appendingPathComponent("first.bin"), size: half)
    let folder = dir.appendingPathComponent("later", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    // Fits the budget comfortably ON ITS OWN — but not after "first.bin".
    _ = try sparseFile(folder.appendingPathComponent("inner.bin"), size: half)
    pb.clearContents()
    pb.writeObjects([loose as NSURL, folder as NSURL])   // loose file consumes first
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "first.bin") }
    else { Issue.record("expected the loose file only, got \(got)") }
    #expect(skipped.get() == ["folder too large to sync: later"])
}

/// Same, file-count arm, with a nonzero `sendable.count`.
@Test @MainActor func folderThatFitsAloneIsRejectedOncePriorFilesAteTheFileCount() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    var urls: [NSURL] = []
    for i in 0..<(ClipboardWatcher.maxFilesPerClip - 1) {          // 499 loose files
        let u = dir.appendingPathComponent("f\(i).txt")
        try Data("x".utf8).write(to: u)
        urls.append(u as NSURL)
    }
    let folder = dir.appendingPathComponent("later", isDirectory: true)
    _ = try writeFile(folder, "a.txt", "a")                        // 2 files: fits alone,
    _ = try writeFile(folder, "b.txt", "b")                        // 499 + 2 > 500 does not
    pb.clearContents()
    pb.writeObjects(urls + [folder as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] {
        #expect(fs.count == ClipboardWatcher.maxFilesPerClip - 1)
        #expect(fs.allSatisfy { $0.relPath == nil })                // no partial tree slipped in
    } else { Issue.record("expected .files, got \(got)") }
    #expect(skipped.get() == ["folder too large to sync: later"])
}

@Test @MainActor func folderOverTheFileCountCapIsSkippedWholly() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let folder = dir.appendingPathComponent("many", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    for i in 0..<(ClipboardWatcher.maxFilesPerClip + 1) {
        try Data("x".utf8).write(to: folder.appendingPathComponent("f\(i).txt"))
    }
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
    #expect(skipped.get() == ["folder too large to sync: many"])
    // The walk bails out one item past the cap, and that truncated prefix is
    // STABLE, so re-copying the same folder yields the same fingerprint and it
    // is neither re-detected nor re-toasted.
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    #expect(skipped.get().count == 1)
}

@Test @MainActor func budgetGreedySkipOverflowFallsBackToSingleFile() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    // Two sparse files just over half the ~49.4 MB budget each: the first fits,
    // the second overflows the cumulative sum and is skipped -> one survivor ->
    // kind "file". Sized off the constant so the boundary tracks the frame cap.
    let each = ClipboardWatcher.fileBudget / 2 + 1
    func sparse(_ name: String) throws -> URL {
        try sparseFile(dir.appendingPathComponent(name), size: each)
    }
    let f1 = try sparse("big1.bin"); let f2 = try sparse("big2.bin")
    pb.clearContents(); pb.writeObjects([f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "big1.bin") }
    else { Issue.record("expected single-file fallback, got \(got)") }
    #expect(skipped.get().contains { $0.contains("skipped") })
}

@Test @MainActor func maxFilesCapEmitsAtMostFiveHundred() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    var urls: [NSURL] = []
    for i in 0..<(ClipboardWatcher.maxFilesPerClip + 1) {
        let u = dir.appendingPathComponent("f\(i).txt")
        try Data("x".utf8).write(to: u)
        urls.append(u as NSURL)
    }
    pb.clearContents(); pb.writeObjects(urls)
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] { #expect(fs.count == 500) }
    else { Issue.record("expected .files") }
    #expect(ClipboardWatcher.maxFilesPerClip == 500)   // in lockstep with Python
    #expect(skipped.get() == ["1 file(s) skipped (too large to sync)"])
}

@Test @MainActor func updateLocalFilesWritesUniquifiedAndDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let received = tempDir()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
    let placed = watcher.updateLocalFiles([
        (name: "dup.txt", data: Data("1".utf8), relPath: nil),
        (name: "dup.txt", data: Data("2".utf8), relPath: nil),
    ])
    #expect(placed.count == 2)
    #expect(placed.map(\.name) == ["dup.txt", "dup (2).txt"])
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup.txt").path))
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup (2).txt").path))
    // Placement baselines the fingerprint list, so the next poll does not echo.
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}
