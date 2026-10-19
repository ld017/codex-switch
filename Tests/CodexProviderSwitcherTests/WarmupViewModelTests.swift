import XCTest
import CodexProfileCore
@testable import CodexProviderSwitcher

final class WarmupViewModelTests: XCTestCase {
    private func date(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value)!
    }

    @MainActor
    func testModeSwitchKeepsCanonicalResetAndTargetDateAcrossMidnight() throws {
        let model = WarmupViewModel(now: { self.date("2026-10-18T12:00:00Z") })
        model.profiles = [.init(id: "uuid-A", label: "A", isAvailable: true)]
        model.draft.accounts = ["uuid-A"]
        model.draft.mode = .reset
        model.draft.time = "04:00"
        model.draft.repeatRule = .once
        model.draft.targetDate = "2026-10-20"
        model.setMode(.warmup)
        XCTAssertEqual(model.draft.time, "23:00")
        XCTAssertEqual(model.draft.targetDate, "2026-10-20")
        let plan = try model.makePlan()
        XCTAssertEqual(plan.targetResetTime, .init(hour: 4, minute: 0))
        XCTAssertEqual(plan.profileIDs, ["uuid-A"])
        XCTAssertTrue(model.preview.contains("10月19日 23:00"))
        XCTAssertTrue(model.preview.contains("10月20日 04:00"))
    }

    @MainActor
    func testSingleRunRejectsPastTriggerEvenWhenTargetResetIsFuture() {
        let model = WarmupViewModel(now: { self.date("2026-10-19T02:00:00Z") })
        model.profiles = [.init(id: "a", label: "A", isAvailable: true)]
        model.draft.accounts = ["a"]
        model.draft.repeatRule = .once
        model.draft.enabled = true
        model.draft.mode = .reset
        model.draft.time = "12:00"
        model.draft.targetDate = "2026-10-19"
        XCTAssertThrowsError(try model.makePlan())
    }

    @MainActor
    func testExpiredPlanCanBeDisabledEvenWhenItsSavedAccountWasRemoved() throws {
        let model = WarmupViewModel(now: { self.date("2026-10-19T02:00:00Z") })
        model.loadSavedPlan(.init(id: "persisted", enabled: true, recurrence: .once,
            targetResetDate: .init(year: 2026, month: 10, day: 19), targetResetTime: .init(hour: 12, minute: 0), profileIDs: ["removed"]))
        model.draft.enabled = false
        let paused = try model.makePlan()
        XCTAssertFalse(paused.enabled)
        XCTAssertEqual(paused.profileIDs, ["removed"])
        XCTAssertFalse(model.canRun)
        model.draft.enabled = true
        XCTAssertThrowsError(try model.makePlan())
    }

    @MainActor
    func testUnavailableSelectionAndMoreThanFourAccountsCannotSave() {
        let model = WarmupViewModel()
        model.profiles = (1...5).map { .init(id: "id-\($0)", label: "A\($0)", isAvailable: $0 != 1) }
        model.draft.accounts = ["id-1"]
        XCTAssertThrowsError(try model.makePlan())
        model.profiles[0] = .init(id: "id-1", label: "A1", isAvailable: true)
        model.draft.accounts = Set((1...5).map { "id-\($0)" })
        XCTAssertThrowsError(try model.makePlan())
    }

    @MainActor
    func testSaveFailureDoesNotReplaceSavedPlanOrDiscardDraft() {
        let model = WarmupViewModel()
        model.profiles = [.init(id: "a", label: "A", isAvailable: true)]
        model.draft.accounts = ["a"]
        let old = WarmupPlan(id: "persisted", enabled: false, recurrence: .daily,
                             targetResetTime: .init(hour: 9, minute: 0), profileIDs: ["a"])
        model.loadSavedPlan(old)
        model.draft.enabled = true
        model.onSave = { _ in throw NSError(domain: "disk", code: 1) }
        model.saveDraft()
        XCTAssertEqual(model.savedPlan, old)
        XCTAssertTrue(model.draft.enabled)
        XCTAssertTrue(model.hasUnsavedChanges)
    }

    @MainActor
    func testImmediateRunKeepsPlanDisabledAndUsesOnlySelectedStableIDs() {
        let model = WarmupViewModel()
        model.profiles = [.init(id: "uuid-B", label: "B", isAvailable: true), .init(id: "uuid-A", label: "A", isAvailable: true)]
        model.draft.accounts = ["uuid-B"]
        var requested: [String] = []
        model.onRun = { requested = $0 }
        model.runNow()
        XCTAssertEqual(requested, ["uuid-B"])
        XCTAssertFalse(model.draft.enabled)
        XCTAssertNil(model.savedPlan)
        XCTAssertTrue(model.results.isEmpty)
    }

    @MainActor
    func testControlLoadErrorBlocksSaveAndRunUntilSuccessfulReload() {
        let model = WarmupViewModel()
        model.profiles = [.init(id: "a", label: "A", isAvailable: true)]
        model.draft.accounts = ["a"]
        var invoked = false
        model.onSave = { invoked = true; return $0 }
        model.onRun = { _ in invoked = true }
        model.setControlLoadError("控制文件损坏")
        model.saveDraft()
        model.runNow()
        XCTAssertFalse(invoked)
        XCTAssertNotNil(model.controlLoadError)
        XCTAssertFalse(model.lastMessage.isEmpty)
    }
}
