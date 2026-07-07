import Testing
import Foundation
import AppKit
@testable import AnyClipDaemon
@testable import AnyClipCore

private func privatePasteboard() -> NSPasteboard {
    NSPasteboard(name: NSPasteboard.Name("anyclip-outq-\(UUID().uuidString)"))
}

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-outq-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

// PRIMARY REGRESSION for the v1.1.10 outbound-silence wedge.
//
// A send that NEVER completes (a leaked NWConnection.send continuation after a
// peer restart / tie-break, which no timeout could free) must not stop the
// clipboard poll loop from detecting the NEXT copy. Detection is now decoupled
// from send completion: the watcher's onChange only ENQUEUES, and a separate
// task drains the queue. Here that drain task wedges FOREVER on the first
// payload; the watcher must still detect the second.
//
// Before the fix (onChange awaited link.sendClip inline in the single poll
// loop, and there was no OutboundQueue), the first hung send froze pollOnce
// mid-iteration and the second copy was NEVER detected -> outbound went
// permanently silent until an app restart. After the fix -> both are detected.
@Test @MainActor func stalledSendNeverFreezesClipboardDetection() async throws {
    let pb = privatePasteboard()
    let queue = OutboundQueue()
    let detected = Locked<[ClipPayload]>([])
    let sendStarted = Locked(0)

    // Production wiring: onChange DETECTS then hands off (non-blocking enqueue).
    let watcher = ClipboardWatcher(
        pasteboard: pb, pollInterval: 0.05, receivedDir: tempDir(),
        callbacks: ClipboardWatcher.Callbacks(
            onChange: { payload in
                detected.set(detected.get() + [payload])
                queue.enqueue(payload)
            }))

    // Drain task wedges FOREVER on the first payload — a send that never
    // completes (like a leaked continuation no timeout can free). Park in a
    // cancellation-aware loop, NOT Task.sleep(.max): a `.max` deadline
    // overflows and can return immediately on some platforms (it did in CI),
    // which would let the drain process the second payload and defeat the test.
    let sender = Task {
        await queue.run { _ in
            sendStarted.set(sendStarted.get() + 1)
            while !Task.isCancelled { try? await Task.sleep(nanoseconds: 20_000_000) }
        }
    }
    defer { sender.cancel(); queue.finish() }

    // Copy #1: detected, enqueued, drain parks on it forever.
    pb.clearContents(); pb.setString("first-unique", forType: .string)
    await watcher.pollOnceForTesting()

    // Deterministically wait until the drain has STARTED sending #1 (then it is
    // wedged on it) — no reliance on a fixed sleep that could be too short.
    for _ in 0..<200 {
        if sendStarted.get() == 1 { break }
        try await Task.sleep(nanoseconds: 10_000_000)
    }
    #expect(sendStarted.get() == 1)        // drain is now wedged on #1

    // Copy #2: MUST still be detected — the poll loop is not frozen.
    pb.clearContents(); pb.setString("second-unique", forType: .string)
    await watcher.pollOnceForTesting()

    #expect(detected.get().count == 2)     // core guarantee: detection never froze
    #expect(sendStarted.get() == 1)        // drain still wedged on #1, never reached #2
    if case .text(let s)? = detected.get().last { #expect(s == "second-unique") }
    else { Issue.record("expected the second text payload") }
}

// enqueue must be non-blocking even when the drain task is wedged, and must not
// throw / block once the buffer is full (bufferingNewest drops the oldest).
@Test func enqueueNeverBlocksWhenDrainIsWedged() async throws {
    let queue = OutboundQueue(bufferSize: 4)
    let sender = Task {
        await queue.run { _ in
            while !Task.isCancelled { try? await Task.sleep(nanoseconds: 20_000_000) }
        }
    }
    defer { sender.cancel(); queue.finish() }

    // Far more than the buffer; each call must return promptly (no await/hang).
    let start = Date()
    for i in 0..<10_000 { queue.enqueue(.text("clip-\(i)")) }
    #expect(Date().timeIntervalSince(start) < 2.0)
}

// FIFO drain: with an instantly-returning send, every enqueued payload is
// delivered in order (guards the scheduling change against reordering/loss).
@Test func drainDeliversInFifoOrder() async throws {
    let queue = OutboundQueue()
    let seen = Locked<[String]>([])
    let sender = Task {
        await queue.run { payload in
            if case .text(let s) = payload { seen.set(seen.get() + [s]) }
        }
    }
    for i in 0..<50 { queue.enqueue(.text("m\(i)")) }
    // Wait for the drain to catch up, then finish.
    for _ in 0..<200 {
        if seen.get().count == 50 { break }
        try await Task.sleep(nanoseconds: 5_000_000)
    }
    queue.finish()
    _ = await sender.value
    #expect(seen.get() == (0..<50).map { "m\($0)" })
}
