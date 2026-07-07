import Foundation
import AnyClipCore

/// Decouples clipboard *detection* from send *completion*.
///
/// The clipboard watcher runs a single sequential poll loop; before this type
/// existed, its `onChange` callback awaited the peer send inline, so one send
/// that stalled, errored, timed out, or leaked its NWConnection continuation
/// froze the loop mid-iteration and killed ALL future polling (the v1.1.10
/// outbound-silence wedge). Here the watcher only ENQUEUES (a non-blocking
/// `yield`), and a dedicated task drains the queue and performs the guarded
/// send. A send that never returns can therefore stall ONLY the drain task —
/// never the producer — so clipboard detection is structurally incapable of
/// being frozen by any send outcome.
///
/// This is a send-*scheduling* change only: the same frames go out in the same
/// FIFO order, so the wire format / golden vectors / interop are unaffected.
public final class OutboundQueue: @unchecked Sendable {
    private let stream: AsyncStream<ClipPayload>
    private let cont: AsyncStream<ClipPayload>.Continuation

    /// `bufferingNewest` bounds memory if the sender backs up: if the link is
    /// wedged we keep the most recent clips and drop the oldest, which is the
    /// right behaviour for a clipboard mirror (only the latest copy matters).
    public init(bufferSize: Int = 64) {
        (stream, cont) = AsyncStream.makeStream(
            of: ClipPayload.self,
            bufferingPolicy: .bufferingNewest(bufferSize))
    }

    /// Non-blocking; safe to call from the @MainActor watcher poll loop. Never
    /// awaits a send, so it can never freeze the caller.
    public func enqueue(_ payload: ClipPayload) {
        cont.yield(payload)
    }

    /// End the stream so a `run` consumer returns. Idempotent.
    public func finish() {
        cont.finish()
    }

    /// Drain the queue, invoking `send` per payload in FIFO order. Returns when
    /// the queue finishes OR the surrounding task is cancelled (AsyncStream
    /// iteration resumes with `nil` on cancellation). A `send` that never
    /// returns stalls ONLY this task.
    public func run(send: @Sendable (ClipPayload) async -> Void) async {
        for await payload in stream {
            if Task.isCancelled { break }
            await send(payload)
        }
    }
}
