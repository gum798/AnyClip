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
    #expect(skipped.get().count == 1)
    #expect(skipped.get()[0].contains("folders are not supported"))
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
