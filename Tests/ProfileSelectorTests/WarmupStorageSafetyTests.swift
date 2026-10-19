import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup storage safety")
struct WarmupStorageSafetyTests {
    @Test("result pruning refuses a symlink in any ancestor without deleting the target")
    func linkedResultAncestor() throws {
        let base = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-safety-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let realRoot = base.appendingPathComponent("outside/warmup")
        let result = realRoot.appendingPathComponent("results/" + UUID().uuidString + ".json")
        try FileManager.default.createDirectory(at: result.deletingLastPathComponent(), withIntermediateDirectories: true)
        try Data("important".utf8).write(to: result)
        let linkedParent = base.appendingPathComponent("linked")
        try FileManager.default.createSymbolicLink(at: linkedParent, withDestinationURL: base.appendingPathComponent("outside"))
        #expect(throws: WarmupOwnerError.self) {
            _ = try WarmupMaintenance.pruneResults(root: linkedParent.appendingPathComponent("warmup"), maxCount: 0)
        }
        #expect(FileManager.default.fileExists(atPath: result.path))
    }

    @Test("store rejects linked lock before open can create its external target")
    func lockRejectedBeforeOpen() throws {
        let base = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-safety-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: base) }
        let root = base.appendingPathComponent("warmup")
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let outside = base.appendingPathComponent("must-not-create.lock")
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("control.lock"), withDestinationURL: outside)
        #expect(throws: WarmupOwnerError.self) { _ = try WarmupStore(rootURL: root).replacePlan(nil) }
        #expect(!FileManager.default.fileExists(atPath: outside.path))
    }

    @Test("zero-history compaction retains recent unknown and blocks another trigger of same identity")
    func compactRecentUnknown() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-safety-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let control = try store.replacePlan(.init(id: "default", recurrence: .daily, targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["one", "alias"]))
        let now = Date()
        let key = WarmupExecutionKey(planID: "default", triggerDate: now, profileID: "one", accountIdentity: "same")
        _ = try store.claimSending(key: key, planRevision: control.planRevision, identityVersion: "v1", now: now)
        _ = try store.update(key: key, identityVersion: "v1", status: .unknown, now: now)
        let compacted = try store.compact(keepTerminalPerWatermark: 0)
        #expect(compacted.runs.contains { $0.key == key && $0.status == .unknown })
        #expect(compacted.executionWatermarks[key.watermarkKey] == key.triggerDate)
        #expect(throws: WarmupStoreError.self) {
            _ = try store.claimSending(key: .init(planID: "default", triggerDate: now.addingTimeInterval(60), profileID: "alias", accountIdentity: "same"), planRevision: control.planRevision, identityVersion: "v1", now: now.addingTimeInterval(60))
        }
    }

    @Test("expired unknown may compact while original execution watermark remains")
    func compactExpiredUnknown() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-safety-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let control = try store.replacePlan(.init(id: "default", recurrence: .daily, targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["one"]))
        let past = Date().addingTimeInterval(-6 * 3600)
        let key = WarmupExecutionKey(planID: "default", triggerDate: past, profileID: "one", accountIdentity: "same")
        _ = try store.recordOutcome(key: key, planRevision: control.planRevision, identityVersion: "v1", status: .unknown, now: past)
        let compacted = try store.compact(keepTerminalPerWatermark: 0)
        #expect(compacted.runs.isEmpty)
        #expect(compacted.executionWatermarks[key.watermarkKey] == key.triggerDate)
        #expect(throws: WarmupStoreError.self) { _ = try store.claimSending(key: key, planRevision: control.planRevision, identityVersion: "v1") }
    }

    @Test("untrusted diagnostic text is bounded at both outcome and update boundaries")
    func boundedReason() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-safety-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let c = try store.replacePlan(.init(id: "default", recurrence: .daily, targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["one", "two"]))
        let reason = String(repeating: "错误🧪", count: 2000)
        let now = Date()
        let first = WarmupExecutionKey(planID: "default", triggerDate: now, profileID: "one", accountIdentity: "one")
        let second = WarmupExecutionKey(planID: "default", triggerDate: now, profileID: "two", accountIdentity: "two")
        _ = try store.recordOutcome(key: first, planRevision: c.planRevision, identityVersion: "v1", status: .failed, reason: reason)
        _ = try store.claimSending(key: second, planRevision: c.planRevision, identityVersion: "v1")
        _ = try store.update(key: second, identityVersion: "v1", status: .unknown, reason: reason)
        let loaded = try store.load()
        #expect(loaded.runs.count == 2)
        #expect(loaded.runs.allSatisfy { ($0.reason?.utf8.count ?? 0) <= 2048 })
        #expect(loaded.runs.allSatisfy { $0.reason?.hasPrefix("错误🧪") == true })
    }
}
