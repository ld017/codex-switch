import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup recovery boundaries")
struct WarmupRecoveryTests {
    @Test("same identity in another profile cannot send the same slot twice")
    func duplicateIdentity() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let p = WarmupPlan(id: "default", recurrence: .daily, targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["1", "2"])
        let c = try store.replacePlan(p)
        let t = Date()
        _ = try store.claimSending(key: .init(planID: "default", triggerDate: t, profileID: "1", accountIdentity: "same"), planRevision: c.planRevision, identityVersion: "same")
        #expect(throws: WarmupStoreError.self) {
            _ = try store.claimSending(key: .init(planID: "default", triggerDate: t, profileID: "2", accountIdentity: "same"), planRevision: c.planRevision, identityVersion: "same")
        }
    }

    @Test("restart retires possible sends as unknown without reopening them")
    func recovery() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let c = try store.replacePlan(.init(id: "default", recurrence: .daily, targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["1"]))
        let key = WarmupExecutionKey(planID: "default", triggerDate: Date(), profileID: "1", accountIdentity: "one")
        _ = try store.claimSending(key: key, planRevision: c.planRevision, identityVersion: "one")
        let recovered = try store.recoverInterruptedRuns()
        #expect(recovered.runs.first?.status == .unknown)
        _ = try store.compact(keepTerminalPerWatermark: 0)
        #expect(throws: WarmupStoreError.self) {
            _ = try store.claimSending(key: key, planRevision: c.planRevision, identityVersion: "one")
        }
    }
}
