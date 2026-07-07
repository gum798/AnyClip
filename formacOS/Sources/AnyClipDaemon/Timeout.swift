import Foundation

public struct TimeoutError: Error, Equatable {}

/// Race `operation` against a deadline. NOTE: if the operation is stuck in
/// a non-cancellable continuation (NWConnection receive), the loser task
/// only ends when the caller cancels the underlying connection — always
/// cancel the connection after catching TimeoutError.
public func withTimeout<T: Sendable>(
    seconds: Double,
    operation: @escaping @Sendable () async throws -> T
) async throws -> T {
    try await withThrowingTaskGroup(of: T.self) { group in
        group.addTask { try await operation() }
        group.addTask {
            try await Task.sleep(nanoseconds: UInt64(seconds * 1_000_000_000))
            throw TimeoutError()
        }
        // cancelAll on EVERY exit path (including the timeout throw at
        // group.next()) and BEFORE the group drains its children on scope exit,
        // so the losing child is always signalled to cancel as early as
        // possible. NOTE: cancellation only frees the group if the losing child
        // actually finishes when cancelled — a continuation that ignores
        // cancellation would still make the implicit drain hang. The real
        // guarantee lives in FramedConnection.rawSendFrame, which now resumes
        // its continuation from onCancel; this defer only ensures the signal is
        // sent promptly.
        defer { group.cancelAll() }
        return try await group.next()!
    }
}

/// Tiny thread-safe box used by connection callbacks and tests.
public final class Locked<T>: @unchecked Sendable {
    private var value: T
    private let lock = NSLock()
    public init(_ initial: T) { value = initial }
    public func get() -> T { lock.lock(); defer { lock.unlock() }; return value }
    public func set(_ new: T) { lock.lock(); defer { lock.unlock() }; value = new }
    public func exchange(_ new: T) -> T {
        lock.lock(); defer { lock.unlock() }
        let old = value; value = new; return old
    }
}
