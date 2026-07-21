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

@Test @MainActor func folderOnClipboardIsSkippedWithToastOnce() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let folder = tempDir() // a real directory
    pb.clearContents()
    pb.writeObjects([folder as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
    // Exactly one skip callback, singular wording naming the folder.
    #expect(skipped.get().count == 1)
    #expect(skipped.get()[0]
        == "folder not synced — folders are not supported: \(folder.lastPathComponent)")
    // Same copy is never re-detected.
    await watcher.pollOnceForTesting()
    #expect(skipped.get().count == 1)
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

@Test @MainActor func oversizedFileIsSkipped() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let file = tempDir().appendingPathComponent("big.bin")
    FileManager.default.createFile(atPath: file.path, contents: nil)
    let handle = try FileHandle(forWritingTo: file)
    try handle.truncate(atOffset: UInt64(12 * 1024 * 1024)) // > ~11.6MB budget
    try handle.close()
    pb.clearContents()
    pb.writeObjects([file as NSURL])
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
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

@Test @MainActor func folderMixedWithFilesSkipsFolderSyncsFiles() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let folder = dir.appendingPathComponent("sub", isDirectory: true)
    try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
    let f1 = dir.appendingPathComponent("a.txt"); try Data("one".utf8).write(to: f1)
    let f2 = dir.appendingPathComponent("b.txt"); try Data("two".utf8).write(to: f2)
    pb.clearContents()
    pb.writeObjects([folder as NSURL, f1 as NSURL, f2 as NSURL])
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] { #expect(fs.count == 2) } else { Issue.record("expected .files") }
    // Single folder -> exactly one skip callback, singular wording with the name.
    #expect(skipped.get().count == 1)
    #expect(skipped.get()[0] == "folder not synced — folders are not supported: sub")
}

@Test @MainActor func multipleFoldersEmitOneAggregatedSkip() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    let d1 = dir.appendingPathComponent("one", isDirectory: true)
    let d2 = dir.appendingPathComponent("two", isDirectory: true)
    try FileManager.default.createDirectory(at: d1, withIntermediateDirectories: true)
    try FileManager.default.createDirectory(at: d2, withIntermediateDirectories: true)
    let f1 = dir.appendingPathComponent("keep.txt"); try Data("k".utf8).write(to: f1)
    pb.clearContents()
    pb.writeObjects([d1 as NSURL, d2 as NSURL, f1 as NSURL])
    await watcher.pollOnceForTesting()
    // The single accepted file still syncs (legacy .file kind).
    let got = changes.get()
    #expect(got.count == 1)
    if case .file(let name, _) = got[0] { #expect(name == "keep.txt") }
    else { Issue.record("expected single-file payload") }
    // Exactly ONE aggregated skip notification, plural wording, no folder names.
    #expect(skipped.get().count == 1)
    #expect(skipped.get()[0] == "2 folders not synced — folders are not supported")
}

@Test @MainActor func budgetGreedySkipOverflowFallsBackToSingleFile() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    // Two 7 MiB sparse files: the first fits the ~11.65 MB budget, the second
    // overflows the cumulative sum and is skipped -> one survivor -> kind "file".
    func sparse(_ name: String) throws -> URL {
        let u = dir.appendingPathComponent(name)
        FileManager.default.createFile(atPath: u.path, contents: nil)
        let h = try FileHandle(forWritingTo: u)
        try h.truncate(atOffset: UInt64(7 * 1024 * 1024)); try h.close()
        return u
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

@Test @MainActor func maxFilesCapEmitsAtMostOneHundred() async throws {
    let pb = privatePasteboard()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: tempDir(), changes: changes, skipped: skipped)
    let dir = tempDir()
    var urls: [NSURL] = []
    for i in 0..<101 {
        let u = dir.appendingPathComponent("f\(i).txt")
        try Data("x".utf8).write(to: u)
        urls.append(u as NSURL)
    }
    pb.clearContents(); pb.writeObjects(urls)
    await watcher.pollOnceForTesting()
    let got = changes.get()
    #expect(got.count == 1)
    if case .files(let fs) = got[0] { #expect(fs.count == 100) } else { Issue.record("expected .files") }
    #expect(skipped.get().contains { $0.contains("skipped") })
}

@Test @MainActor func updateLocalFilesWritesUniquifiedAndDoesNotEcho() async throws {
    let pb = privatePasteboard()
    let received = tempDir()
    let changes = Locked<[ClipPayload]>([]); let skipped = Locked<[String]>([])
    let watcher = makeWatcher(pb, received: received, changes: changes, skipped: skipped)
    let placed = watcher.updateLocalFiles([
        (name: "dup.txt", data: Data("1".utf8)),
        (name: "dup.txt", data: Data("2".utf8)),
    ])
    #expect(placed.count == 2)
    #expect(placed.map(\.name) == ["dup.txt", "dup (2).txt"])
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup.txt").path))
    #expect(FileManager.default.fileExists(atPath: received.appendingPathComponent("dup (2).txt").path))
    // Placement baselines the fingerprint list, so the next poll does not echo.
    await watcher.pollOnceForTesting()
    #expect(changes.get().isEmpty)
}
