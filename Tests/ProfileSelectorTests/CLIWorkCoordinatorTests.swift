import Foundation
import Testing
@testable import CodexProfileCore

/// These tests exercise permits and actual concurrent closures. The latches
/// only control when a real operation finishes; they do not replace the
/// coordinator or inspect its private queue.
private actor CLIConcurrencyProbe {
    private var active = 0
    private(set) var maximum = 0
    private(set) var events: [String] = []

    func enter(_ event: String) {
        active += 1
        maximum = max(maximum, active)
        events.append(event)
    }

    func leave() { active -= 1 }
    func record(_ event: String) { events.append(event) }
}

private actor CLITestLatch {
    private var isOpen = false
    private var waiters: [CheckedContinuation<Void, Never>] = []

    func wait() async {
        if isOpen { return }
        await withCheckedContinuation { waiters.append($0) }
    }

    func open() {
        isOpen = true
        let pending = waiters
        waiters.removeAll()
        for waiter in pending { waiter.resume() }
    }
}

private func coordinatorEventually(
    timeout: Duration = .seconds(2),
    _ predicate: () async -> Bool
) async -> Bool {
    let clock = ContinuousClock()
    let deadline = clock.now.advanced(by: timeout)
    while clock.now < deadline {
        if await predicate() { return true }
        try? await Task.sleep(for: .milliseconds(2))
    }
    return await predicate()
}

private enum CLITestFailure: Error { case operationFailed }

@Suite("CLI work coordinator")
struct CLIWorkCoordinatorTests {
    // Break caught: an ordinary permit that grants while another is active.
    @Test("ordinary operations never overlap")
    func ordinaryIsSingleConcurrency() async throws {
        let coordinator = CLIWorkCoordinator()
        let probe = CLIConcurrencyProbe()
        try await withThrowingTaskGroup(of: Void.self) { group in
            for index in 0..<12 {
                group.addTask {
                    try await coordinator.withOrdinary {
                        await probe.enter("ordinary-\(index)")
                        try await Task.sleep(for: .milliseconds(5))
                        await probe.leave()
                    }
                }
            }
            try await group.waitForAll()
        }
        #expect(await probe.maximum == 1)
        #expect(await probe.events.count == 12)
    }

    // Break caught: batch granted before an ordinary drains, or new ordinary
    // callers overtaking an already-waiting batch.
    @Test("queued batch drains ordinary and blocks later ordinary work")
    func batchIsExclusiveAndFIFO() async throws {
        let coordinator = CLIWorkCoordinator()
        let ordinary = try await coordinator.acquireOrdinary()
        let probe = CLIConcurrencyProbe()
        let batchGate = CLITestLatch()
        let batch = Task {
            try await coordinator.withWarmupBatch {
                await probe.record("batch")
                await batchGate.wait()
            }
        }
        // Let the batch enter the actor's suspended-acquisition path while the
        // first ordinary permit is held, before submitting later ordinary work.
        try await Task.sleep(for: .milliseconds(25))
        let next = Task {
            try await coordinator.withOrdinary { await probe.record("ordinary") }
        }
        try await Task.sleep(for: .milliseconds(25))
        #expect(await probe.events.isEmpty)
        #expect(await coordinator.release(ordinary))
        #expect(await coordinatorEventually { await probe.events.contains("batch") })
        #expect(await probe.events == ["batch"])
        await batchGate.open()
        try await batch.value
        try await next.value
        #expect(await probe.events == ["batch", "ordinary"])
    }

    // Break caught: a foreign or already-released permit freeing the current
    // owner's slot (including a permit originating at a different actor).
    @Test("only the active owner token releases a permit")
    func releaseIsOwnerChecked() async throws {
        let coordinator = CLIWorkCoordinator()
        let other = CLIWorkCoordinator()
        let owner = try await coordinator.acquireOrdinary()
        let foreign = try await other.acquireOrdinary()
        #expect(await coordinator.release(foreign) == false)
        #expect(await other.release(foreign))
        #expect(await coordinator.release(owner))
        let replacement = try await coordinator.acquireBatch()
        #expect(await coordinator.release(owner) == false)
        let probe = CLIConcurrencyProbe()
        let waiting = Task {
            try await coordinator.withOrdinary { await probe.record("entered") }
        }
        try await Task.sleep(for: .milliseconds(25))
        #expect(await probe.events.isEmpty)
        #expect(await coordinator.release(replacement))
        try await waiting.value
        #expect(await probe.events == ["entered"])
    }

    // Break caught: throwing closures skip release and strand later work.
    @Test("scoped ordinary and batch permits release after thrown errors")
    func scopedErrorsRelease() async throws {
        let coordinator = CLIWorkCoordinator()
        do {
            try await coordinator.withOrdinary { () async throws -> Void in
                throw CLITestFailure.operationFailed
            }
            Issue.record("throwing operation unexpectedly succeeded")
        } catch CLITestFailure.operationFailed {}
        do {
            try await coordinator.withWarmupBatch { () async throws -> Void in
                throw CLITestFailure.operationFailed
            }
            Issue.record("throwing batch unexpectedly succeeded")
        } catch CLITestFailure.operationFailed {}
        let value = try await coordinator.withOrdinary { 42 }
        #expect(value == 42)
    }

    // Break caught: a cancelled suspended continuation remains in the queue,
    // never returns CancellationError, or consumes the next permit.
    @Test("cancelled queued ordinary and batch waiters leave no reservation")
    func queuedCancellationDoesNotLeak() async throws {
        for isBatch in [false, true] {
            let coordinator = CLIWorkCoordinator()
            let owner = try await coordinator.acquireOrdinary()
            let probe = CLIConcurrencyProbe()
            let waiter = Task {
                do {
                    let permit = try await (isBatch ? coordinator.acquireBatch() : coordinator.acquireOrdinary())
                    _ = await coordinator.release(permit)
                    await probe.record("unexpected-grant")
                } catch is CancellationError {
                    await probe.record("cancelled")
                } catch {
                    await probe.record("unexpected-error")
                }
            }
            try await Task.sleep(for: .milliseconds(25))
            waiter.cancel()
            #expect(await coordinatorEventually { await probe.events == ["cancelled"] })
            #expect(await coordinator.release(owner))
            await waiter.value
            let next = try await coordinator.acquireOrdinary()
            #expect(await coordinator.release(next))
        }
    }

    // Break caught: cancellation removes an operation's active permit before
    // its asynchronous cleanup has returned.
    @Test("cancellation keeps active ownership until operation cleanup finishes")
    func cancellationWaitsForCleanup() async throws {
        let coordinator = CLIWorkCoordinator()
        let cleanupGate = CLITestLatch()
        let probe = CLIConcurrencyProbe()
        let task = Task {
            try await coordinator.withOrdinary {
                await probe.record("active")
                do {
                    try await Task.sleep(for: .seconds(60))
                } catch {
                    await probe.record("cleanup")
                    await cleanupGate.wait()
                    throw error
                }
            }
        }
        #expect(await coordinatorEventually { await probe.events.contains("active") })
        task.cancel()
        #expect(await coordinatorEventually { await probe.events.contains("cleanup") })
        let next = Task {
            try await coordinator.withWarmupBatch { await probe.record("next") }
        }
        try await Task.sleep(for: .milliseconds(25))
        #expect(await probe.events == ["active", "cleanup"])
        await cleanupGate.open()
        do { try await task.value } catch is CancellationError {}
        try await next.value
        #expect(await probe.events == ["active", "cleanup", "next"])
    }

    // Break caught: already-cancelled callers execute a closure or retain a
    // slot. Cancelling from inside the task makes this independent of launch
    // order between the test and child task.
    @Test("already-cancelled callers do not execute operations")
    func preCancelledDoesNotExecute() async throws {
        let coordinator = CLIWorkCoordinator()
        let probe = CLIConcurrencyProbe()
        let task = Task {
            withUnsafeCurrentTask { $0?.cancel() }
            do {
                try await coordinator.withWarmupBatch { await probe.record("executed") }
            } catch is CancellationError {
                await probe.record("cancelled")
            }
        }
        try await task.value
        #expect(await probe.events == ["cancelled"])
        let permit = try await coordinator.acquireOrdinary()
        #expect(await coordinator.release(permit))
    }
}
