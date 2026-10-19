import Foundation

/// Process-local CLI budget shared by ordinary usage reads and warmup batches.
///
/// Ordinary work owns the budget for exactly one short-lived CLI. A warmup
/// batch first waits for earlier work to drain, then owns the entire budget
/// until its closure (including child-process cleanup) returns. Within that
/// batch, the runner owns the policy of at most four senders followed by one
/// verification reader; it must not acquire an ordinary permit recursively.
/// Account leases and coordination with other processes are separate concerns.
public actor CLIWorkCoordinator {
    public static let shared = CLIWorkCoordinator()

    public enum WorkKind: String, Sendable {
        case ordinary
        case warmupBatch
    }

    /// Opaque ownership token. A permit from another coordinator, or a stale
    /// permit released twice, cannot release the current owner's budget.
    public struct Permit: Hashable, Sendable {
        public let kind: WorkKind
        fileprivate let coordinatorID: UUID
        fileprivate let token: UUID
    }

    private struct Waiter {
        let token: UUID
        let kind: WorkKind
        let continuation: CheckedContinuation<Permit, Error>
    }

    private let coordinatorID = UUID()
    private var activePermit: Permit?
    private var waiters: [Waiter] = []
    private var blocked = false

    public enum Failure: LocalizedError, Sendable {
        case unreapedChild
        public var errorDescription: String? { "CLI 子进程未确认退出，已停止后续查询和预热；请退出工具后检查残留进程" }
    }

    /// A failed reap must not turn an occupied process slot into a free one.
    /// There is deliberately no in-process reset: recovery requires restart.
    public func blockFurtherWork() {
        blocked = true
        let pending = waiters
        waiters.removeAll()
        for waiter in pending { waiter.continuation.resume(throwing: Failure.unreapedChild) }
    }

    public init() {}

    public func acquireOrdinary() async throws -> Permit {
        try await acquire(.ordinary)
    }

    public func acquireBatch() async throws -> Permit {
        try await acquire(.warmupBatch)
    }

    /// Must be called only after the operation's CLI and its cleanup have
    /// finished. Cancellation of an active caller does not release ownership
    /// early; the scoped APIs release when the operation actually returns.
    @discardableResult
    public func release(_ permit: Permit) -> Bool {
        guard activePermit == permit else { return false }
        activePermit = nil
        grantNext()
        return true
    }

    public func withOrdinary<T: Sendable>(
        _ operation: @Sendable () async throws -> T
    ) async throws -> T {
        let permit = try await acquireOrdinary()
        defer { release(permit) }
        try Task.checkCancellation()
        return try await operation()
    }

    public func withWarmupBatch<T: Sendable>(
        _ operation: @Sendable () async throws -> T
    ) async throws -> T {
        let permit = try await acquireBatch()
        defer { release(permit) }
        try Task.checkCancellation()
        return try await operation()
    }

    private func acquire(_ kind: WorkKind) async throws -> Permit {
        try Task.checkCancellation()
        guard !blocked else { throw Failure.unreapedChild }
        let token = UUID()
        let permit: Permit = try await withTaskCancellationHandler {
            try Task.checkCancellation()
            return try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Permit, Error>) in
                // Cancellation may already be set before onCancel's actor
                // message gets a turn. Do not enqueue a cancelled caller.
                guard !blocked else {
                    continuation.resume(throwing: Failure.unreapedChild)
                    return
                }
                guard !Task.isCancelled else {
                    continuation.resume(throwing: CancellationError())
                    return
                }
                waiters.append(Waiter(token: token, kind: kind, continuation: continuation))
                grantNext()
            }
        } onCancel: {
            Task { await self.cancelWaiter(token) }
        }

        // release() can grant a waiter at the same time its task is cancelled.
        // If cancellation won before acquisition returns, return the granted
        // permit to the queue instead of leaking an owner nobody will use.
        do {
            try Task.checkCancellation()
        } catch {
            release(permit)
            throw error
        }
        return permit
    }

    private func grantNext() {
        guard !blocked, activePermit == nil, !waiters.isEmpty else { return }
        let next = waiters.removeFirst()
        let permit = Permit(kind: next.kind, coordinatorID: coordinatorID, token: next.token)
        activePermit = permit
        next.continuation.resume(returning: permit)
    }

    private func cancelWaiter(_ token: UUID) {
        guard let index = waiters.firstIndex(where: { $0.token == token }) else {
            // An already-active owner must keep its permit until cleanup ends.
            return
        }
        let waiter = waiters.remove(at: index)
        waiter.continuation.resume(throwing: CancellationError())
        grantNext()
    }
}
