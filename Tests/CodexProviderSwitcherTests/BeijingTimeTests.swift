import AppKit
import CodexProfileCore
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class BeijingTimeTests: XCTestCase {
    private func instant(_ value: String) -> Date { ISO8601DateFormatter().date(from: value)! }

    func testAbsoluteDisplaysUseBeijingAcrossMidnightInsteadOfHostZone() {
        let date = instant("2026-10-31T16:30:00Z")
        XCTAssertEqual(ResetCreditPresentation.text(count: 1, nearestExpiration: date),
            "重置卡 1 张 · 最近到期 11月1日 00:30（北京时间）")
        XCTAssertEqual(DateFormatter.profileDetailTimestamp.string(from: date), "2026-11-01 00:30（北京时间）")
        var s = ConnectionStatusSnapshot()
        s.diagnosticsCheckedAt = date
        let details = ConnectionStatusPresentation.make(snapshot: s, now: date).details
        XCTAssertEqual(details.last?.title, "诊断更新时间：11月1日 00:30:00（北京时间）")
        XCTAssertEqual(WarmupViewModel.display(date), "2026年11月1日 00:30")
    }

    func testBeijingCalendarHasNoUSDaylightSavingShiftAndKeepsEpoch() {
        for (input, expected) in [
            ("2026-03-08T07:00:00Z", "2026-03-08 15:00:00"),
            ("2026-11-01T06:00:00Z", "2026-11-01 14:00:00"),
            ("2026-12-31T16:00:00Z", "2027-01-01 00:00:00")
        ] {
            let date = instant(input), epoch = date.timeIntervalSince1970
            XCTAssertEqual(BeijingTime.format(date, pattern: "yyyy-MM-dd HH:mm:ss"), expected)
            let wire = BeijingTime.iso8601String(date)
            XCTAssertTrue(wire.hasSuffix("+08:00"))
            XCTAssertEqual(ISO8601DateFormatter().date(from: wire)?.timeIntervalSince1970, epoch)
        }
    }

    func testCurrentMonthIsChosenByBeijingDateWhileServiceBucketLabelsStayIntact() throws {
        let now = instant("2026-10-31T16:30:00Z")
        let data = Data("""
        [{"startDate":"2026-10-31","tokens":9000000},
         {"startDate":"2026-11-01","tokens":1000000},
         {"startDate":"2026-11-01","tokens":1000000},
         {"startDate":"2026-11-02","tokens":5000000}]
        """.utf8)
        let days = try JSONDecoder().decode([TokenActivity.Day].self, from: data)
        let activity = TokenActivity(lifetimeTokens: 16000000, dailyUsageBuckets: days)
        let month = TokenUsagePresentation.monthUsage(activity, now: now)
        XCTAssertEqual(month?.tokens, 1000000)
        XCTAssertEqual(month?.through, "2026-11-01")
        let result = TokenUsagePresentation.summary([.init(identity: "fixture", activity: activity, cached: false)], now: now)
        XCTAssertEqual(result.values, "累计：1600.00 万 · 本月：0.01 亿")
        XCTAssertEqual(activity.dailyUsageBuckets?.map(\.startDate), ["2026-10-31", "2026-11-01", "2026-11-01", "2026-11-02"])
    }

    func testCountdownRecomputesFromTheActualCurrentInstantWithoutOffsettingIt() {
        let now = instant("2026-10-31T16:30:00Z"), reset = instant("2026-10-31T18:41:00Z")
        XCTAssertEqual(resetCountdown(from: reset, now: now), "2时11分")
        XCTAssertEqual(resetCountdown(from: reset, now: now.addingTimeInterval(60)), "2时10分")
        XCTAssertEqual(resetCountdown(from: reset, now: reset.addingTimeInterval(1)), "")
        XCTAssertEqual(updatedLabel(from: now, now: now.addingTimeInterval(120)), "2 分钟前更新")
    }
}

@MainActor
final class BeijingWarmupClockTests: XCTestCase {
    func testLiveClockAdvancesWithoutSavingOrChangingTheDraft() {
        var now = ISO8601DateFormatter().date(from: "2026-10-31T15:59:59Z")!
        let model = WarmupViewModel(now: { now })
        let draft = model.draft
        var saves = 0
        model.onSave = { plan in saves += 1; return plan }
        model.refreshClock()
        XCTAssertEqual(model.currentTimeText, "2026年10月31日 23:59:59")
        now = now.addingTimeInterval(1)
        model.refreshClock()
        XCTAssertEqual(model.currentTimeText, "2026年11月1日 00:00:00")
        XCTAssertEqual(model.draft, draft)
        XCTAssertEqual(saves, 0)
    }

    func testQuotaRefreshRecoversWhenSystemClockMovesBackward() {
        let last = Date(timeIntervalSince1970: 1_000)
        XCTAssertFalse(UsageProvider.needsQuotaRefresh(lastRefresh: last, now: last.addingTimeInterval(59)))
        XCTAssertTrue(UsageProvider.needsQuotaRefresh(lastRefresh: last, now: last.addingTimeInterval(60)))
        XCTAssertTrue(UsageProvider.needsQuotaRefresh(lastRefresh: last, now: last.addingTimeInterval(-1)))
    }
}

@MainActor
final class BeijingTokenMenuClockTests: XCTestCase {
    func testOpenTokenDetailMonthUpdatesFromCacheWithoutRebuildingTrackedMenus() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: home) }
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        try FileManager.default.createDirectory(at: paths.switcherHome, withIntermediateDirectories: true)
        let config = AppConfig(profiles: [.init(id: "fixture", label: "Fixture")], activeProfile: "fixture")
        try JSONEncoder().encode(config).write(to: paths.configURL)
        let vault = FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL)
        let store = ProfileStore(authVault: vault, environment: paths.environment)
        let days = try JSONDecoder().decode([TokenActivity.Day].self, from: Data("""
        [{"startDate":"2026-10-31","tokens":2000000}, {"startDate":"2026-11-01","tokens":1000000}]
        """.utf8))
        var snapshot = UsageSnapshot(planType: "plus", creditsRemaining: nil,
            primaryUsedPercent: 0, primaryResetAt: nil, secondaryUsedPercent: 0,
            secondaryResetAt: nil, fetchedAt: Date())
        snapshot.tokenActivity = TokenActivity(lifetimeTokens: 3000000, dailyUsageBuckets: days)
        store.updateStatus("fixture", .available(snapshot))
        var rebuilds = 0
        let manager = AccountManager(currentProvider: { .openai }, beginOperation: { XCTFail("No operation allowed"); return false },
            finishOperation: {}, prepareSharedHistory: { XCTFail("No network allowed") }, showError: { _, _ in },
            onChange: { rebuilds += 1 }, onIconChange: { _ in }, openWarmup: {}, store: store)
        let root = NSMenu()
        manager.appendItems(to: root)
        let details = try XCTUnwrap(root.items.first { $0.title == "Token 用量详情" }?.submenu)
        manager.menuWillOpen(details) // Only populates the cached Token submenu.
        let account = try XCTUnwrap(details.items.first { $0.title == "Fixture" }?.submenu)
        let items = account.items
        let october = ISO8601DateFormatter().date(from: "2026-10-31T15:59:59Z")!
        manager.refreshDisplayedTimes(now: october)
        XCTAssertTrue(account.items.contains { $0.title == "本月：0.02 亿 Token" })
        manager.refreshDisplayedTimes(now: october.addingTimeInterval(1))
        XCTAssertTrue(account.items.contains { $0.title == "本月：0.01 亿 Token" })
        XCTAssertTrue(account.items.contains { $0.title == "已返回记录截至：2026-11-01" })
        XCTAssertTrue(zip(items, account.items).allSatisfy { $0 === $1 })
        XCTAssertEqual(items.count, account.items.count)
        XCTAssertEqual(rebuilds, 0)
    }
}

@MainActor
final class VisibleTimeClockTests: XCTestCase {
    func testClockOnlyEmitsWhileVisibleAndRespondsToClockCorrections() {
        var now = Date(timeIntervalSince1970: 2_000_000_000)
        var emissions: [Date] = []
        let clock = VisibleTimeClock(now: { now }, onTick: { emissions.append($0) })
        defer { clock.stop() }
        clock.clockDidChange()
        XCTAssertTrue(emissions.isEmpty)
        clock.setVisible(true)
        XCTAssertEqual(emissions, [now])
        now = now.addingTimeInterval(-3600)
        clock.clockDidChange()
        XCTAssertEqual(emissions, [now.addingTimeInterval(3600), now])
        clock.setVisible(false)
        now = now.addingTimeInterval(120)
        clock.clockDidChange()
        XCTAssertEqual(emissions.count, 2)
    }
}
