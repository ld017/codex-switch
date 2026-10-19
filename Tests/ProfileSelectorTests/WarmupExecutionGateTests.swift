import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup dispatch boundary")
struct WarmupExecutionGateTests {
    @Test("clock rollback and late setup never authorize a scheduled send")
    func actualSendTimeIsCheckedOnBothSides() throws {
        let trigger = Date(timeIntervalSince1970: 1_800_000_000)
        #expect(throws: WarmupExecutionGate.Error.self) {
            try WarmupExecutionGate.validate(trigger: trigger, now: trigger.addingTimeInterval(-1))
        }
        try WarmupExecutionGate.validate(trigger: trigger, now: trigger.addingTimeInterval(30))
        #expect(throws: WarmupExecutionGate.Error.self) {
            try WarmupExecutionGate.validate(trigger: trigger, now: trigger.addingTimeInterval(30.01))
        }
    }

    @Test("unreaped child poisons queued and future permits even after owner releases")
    func unreapedChildBlocksAllNewWork() async throws {
        let coordinator = CLIWorkCoordinator()
        let permit = try await coordinator.acquireBatch()
        let waiter = Task { try await coordinator.acquireOrdinary() }
        await coordinator.blockFurtherWork()
        await coordinator.release(permit)
        do { _ = try await waiter.value; Issue.record("queued child was permitted") } catch {}
        do { _ = try await coordinator.acquireBatch(); Issue.record("new batch was permitted") } catch {}
    }
}
