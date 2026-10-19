import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup planner and control store")
struct WarmupPlannerStoreTests {
    private let shanghai = TimeZone(identifier: WarmupTimeZone.identifier)!

    private func localDate(_ value: Date) -> WarmupLocalDate {
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = shanghai
        let c = calendar.dateComponents([.year, .month, .day], from: value)
        return WarmupLocalDate(year: c.year!, month: c.month!, day: c.day!)
    }

    private func makePlan(
        recurrence: WarmupRecurrence = .once,
        targetDate: WarmupLocalDate? = WarmupLocalDate(year: 2026, month: 10, day: 20),
        time: WarmupLocalTime = WarmupLocalTime(hour: 2, minute: 0)
    ) -> WarmupPlan {
        WarmupPlan(
            id: "plan-a",
            enabled: true,
            recurrence: recurrence,
            targetResetDate: recurrence == .once ? targetDate : nil,
            targetResetTime: time,
            profileIDs: ["1", "2", "3", "4"])
    }

    @Test("target before 05:00 triggers on the prior local date")
    func crossMidnightTrigger() throws {
        let target = WarmupLocalDate(year: 2026, month: 10, day: 20)
        let trigger = try WarmupPlanner.triggerDate(
            targetResetDate: target,
            targetResetTime: WarmupLocalTime(hour: 2, minute: 0))
        let parts = WarmupPlanner.localComponents(for: trigger)
        #expect(parts.date == WarmupLocalDate(year: 2026, month: 10, day: 19))
        #expect(parts.time == WarmupLocalTime(hour: 21, minute: 0))
    }

    @Test("weekday recurrence uses target reset day, even when trigger is Sunday")
    func weekdayUsesTargetDay() throws {
        let plan = makePlan(recurrence: .weekdays, time: WarmupLocalTime(hour: 2, minute: 0))
        let now = try WarmupPlanner.date(
            WarmupLocalDate(year: 2026, month: 10, day: 18),
            time: WarmupLocalTime(hour: 20, minute: 0))
        let optionalOccurrence = try WarmupPlanner.nextOccurrence(after: now, plan: plan)
        guard let occurrence = optionalOccurrence else {
            Issue.record("expected a weekday occurrence")
            return
        }
        #expect(occurrence.targetResetDate == WarmupLocalDate(year: 2026, month: 10, day: 19))
        #expect(localDate(occurrence.triggerDate) == WarmupLocalDate(year: 2026, month: 10, day: 18))
        #expect(WarmupPlanner.localComponents(for: occurrence.triggerDate).time == WarmupLocalTime(hour: 21, minute: 0))
    }

    @Test("disabled and past one-shot plans do not schedule")
    func disabledAndPastPlans() throws {
        var disabled = makePlan()
        disabled.enabled = false
        let now = try WarmupPlanner.date(
            WarmupLocalDate(year: 2026, month: 10, day: 20),
            time: WarmupLocalTime(hour: 12, minute: 0))
        #expect(try WarmupPlanner.nextOccurrence(after: now, plan: disabled) == nil)

        let past = makePlan(targetDate: WarmupLocalDate(year: 2026, month: 10, day: 19))
        #expect(try WarmupPlanner.nextOccurrence(after: now, plan: past) == nil)
    }

    @Test("Asia/Shanghai remains a fixed no-DST planning zone")
    func fixedTimeZone() throws {
        let plan = makePlan(targetDate: WarmupLocalDate(year: 2026, month: 3, day: 29), time: WarmupLocalTime(hour: 9, minute: 0))
        let occurrence = try WarmupPlanner.occurrence(for: plan.targetResetDate!, plan: plan)
        #expect(TimeZone(identifier: plan.timeZoneIdentifier)?.secondsFromGMT(for: occurrence.triggerDate) == 8 * 60 * 60)
    }

    @Test("control store revisions, sending intent, identity guard and watermark survive reload")
    func storeTransactions() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true)
            .appendingPathComponent("warmup-store-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        var plan = makePlan()
        let saved = try store.replacePlan(plan)
        plan.revision = saved.planRevision
        #expect(saved.planRevision == 1)
        #expect(saved.plan?.revision == 1)

        let trigger = try WarmupPlanner.triggerDate(
            targetResetDate: plan.targetResetDate!, targetResetTime: plan.targetResetTime)
        let key = WarmupExecutionKey(planID: plan.id, triggerDate: trigger, profileID: "1", accountIdentity: "acct-1")
        let now = Date(timeIntervalSince1970: 1_800_000_000)
        _ = try store.claimSending(key: key, planRevision: saved.planRevision, identityVersion: "cred-v1", now: now)
        do {
            _ = try store.claimSending(key: key, planRevision: saved.planRevision, identityVersion: "cred-v1", now: now)
            Issue.record("duplicate sending claim unexpectedly succeeded")
        } catch let error as WarmupStoreError {
            #expect(error == .alreadyClaimed(key))
        }
        _ = try store.update(key: key, identityVersion: "cred-v1", status: .requestCompleted, now: now)
        do {
            _ = try store.update(key: key, identityVersion: "cred-v2", status: .verifying, now: now)
            Issue.record("stale identity unexpectedly updated the run")
        } catch let error as WarmupStoreError {
            #expect(error == .identityVersionMismatch)
        }
        _ = try store.update(key: key, identityVersion: "cred-v1", status: .unknown, reason: "test", now: now)
        let reloaded = try store.load()
        #expect(reloaded.executionWatermarks[key.watermarkKey] == key.triggerDate)
        #expect(reloaded.runs.first?.status == .unknown)
        #expect(reloaded.runs.first?.reason == "test")
    }

    @Test("existing-window decision is terminal and advances the watermark without sending")
    func recordsNoSendDecision() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true)
            .appendingPathComponent("warmup-outcome-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }
        let store = WarmupStore(rootURL: root)
        let plan = try store.replacePlan(makePlan())
        let key = WarmupExecutionKey(
            planID: "plan-a", triggerDate: Date(timeIntervalSince1970: 1_800_000_000),
            profileID: "1", accountIdentity: "acct-1")
        let state = try store.recordOutcome(
            key: key, planRevision: plan.planRevision, identityVersion: "cred-v1",
            status: .existingWindow, reason: "already active")
        #expect(state.status == .existingWindow)
        #expect(try store.load().executionWatermarks[key.watermarkKey] == key.triggerDate)
    }
}
