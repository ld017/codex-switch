import AppKit
import CodexProfileCore
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class QuotaResetPresentationTests: XCTestCase {
    private func instant(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value)!
    }

    func testReportedResetTimeAndCountdownUseSameBeijingInstant() {
        let now = instant("2026-10-20T08:10:00Z")
        let reset = instant("2026-10-20T12:31:00Z")
        let value = QuotaResetPresentation.make(resetAt: reset, now: now, isCached: false)

        XCTAssertEqual(value.text, "20:31 重置 · 余4时21分")
        XCTAssertEqual(value.compactText, "20:31·4时21分")
        XCTAssertEqual(value.state, .future)
        XCTAssertTrue(value.help.contains("2026年10月20日 20:31:00（北京时间）"))
        XCTAssertTrue(value.accessibilityLabel.contains("服务器报告重置"))
    }

    func testCrossDayAndOtherDateKeepTheReportedBeijingDate() {
        let nextDay = QuotaResetPresentation.make(
            resetAt: instant("2026-10-20T16:31:00Z"),
            now: instant("2026-10-20T08:10:00Z"), isCached: false)
        XCTAssertEqual(nextDay.text, "明日00:31 重置 · 余8时21分")

        let otherDate = QuotaResetPresentation.make(
            resetAt: instant("2026-10-22T16:31:00Z"),
            now: instant("2026-10-20T08:10:00Z"), isCached: false)
        XCTAssertEqual(otherDate.text, "10/23 00:31 重置 · 余2天8时")
        XCTAssertTrue(otherDate.help.contains("2026年10月23日 00:31:00（北京时间）"))
    }

    func testCacheMissingAndExpiredStatesRemainExplicit() {
        let now = instant("2026-10-20T08:10:00Z")
        let cached = QuotaResetPresentation.make(
            resetAt: instant("2026-10-20T12:31:00Z"), now: now, isCached: true)
        XCTAssertEqual(cached.text, "缓存20:31 · 余4时21分")
        XCTAssertEqual(cached.compactText, "缓存20:31·余4时21分")
        XCTAssertEqual(cached.state, .future)

        let missing = QuotaResetPresentation.make(resetAt: nil, now: now, isCached: true)
        XCTAssertEqual(missing.text, "缓存·未返回重置时刻")
        XCTAssertEqual(missing.compactText, missing.text)
        XCTAssertEqual(missing.state, .notReturned)
        XCTAssertTrue(missing.help.contains("服务端未返回"))
        XCTAssertEqual(QuotaResetPresentation.make(resetAt: nil, now: now, isCached: false).text, "重置时间未返回")

        let past = QuotaResetPresentation.make(
            resetAt: instant("2026-10-20T08:10:00Z"), now: now, isCached: true)
        XCTAssertEqual(past.text, "缓存16:10 · 待刷新")
        XCTAssertEqual(past.compactText, "缓存16:10·待刷新")
        XCTAssertEqual(past.state, .awaitingRefresh)
        XCTAssertTrue(past.help.contains("等待新读数"))
        XCTAssertFalse(past.text.contains("已重置"))
    }

    func testShortCountdownAndAdvancingNowAreDerivedFromTheSameReportedReset() {
        let reset = instant("2026-10-20T08:10:45Z")
        let first = QuotaResetPresentation.make(
            resetAt: reset, now: instant("2026-10-20T08:10:00Z"), isCached: false)
        let second = QuotaResetPresentation.make(
            resetAt: reset, now: instant("2026-10-20T08:10:01Z"), isCached: false)
        XCTAssertEqual(first.text, "16:10 重置 · 不足1分")
        XCTAssertEqual(second.text, first.text)
    }

    func testCachedQuotaSemanticsDoNotReuseTokenFailureSemantics() {
        let now = instant("2026-10-20T08:10:00Z")
        var snapshot = UsageSnapshot(planType: "plus", creditsRemaining: nil,
            primaryUsedPercent: 20, primaryResetAt: now.addingTimeInterval(3600),
            secondaryUsedPercent: 0, secondaryResetAt: nil, fetchedAt: now)
        XCTAssertFalse(QuotaResetPresentation.isCachedQuota(.available(snapshot)))
        snapshot.tokenActivityError = "token endpoint failed"
        XCTAssertFalse(QuotaResetPresentation.isCachedQuota(.available(snapshot)))
        XCTAssertTrue(QuotaResetPresentation.isCachedQuota(.stale(snapshot)))
        XCTAssertTrue(QuotaResetPresentation.isCachedQuota(.reloginNeeded(snapshot)))
        XCTAssertFalse(QuotaResetPresentation.isCachedQuota(.stale(nil)))
        XCTAssertFalse(QuotaResetPresentation.isCachedQuota(.reloginNeeded(nil)))
    }

    func testCachedLongDateHasACompactCandidateThatFitsWithoutDroppingState() {
        let value = QuotaResetPresentation.make(
            resetAt: instant("2026-11-20T16:31:00Z"),
            now: instant("2026-10-20T08:10:00Z"), isCached: true)
        XCTAssertNotEqual(value.text, value.compactText)
        XCTAssertTrue(value.compactText.contains("缓存"))
        XCTAssertTrue(value.compactText.contains("11/21 00:31"))
        XCTAssertTrue(value.compactText.contains("余"))
        let font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        let width = NSString(string: value.compactText).size(withAttributes: [.font: font]).width
        XCTAssertLessThanOrEqual(width, AccountMenuLayout.primaryTimeWidth)
    }

    func testUsageWindowLabelsUseRealDurationWithoutInventingFiveHours() {
        XCTAssertEqual(usageWindowLabel(durationMins: 300, legacyLabel: "主窗口"), "5小时")
        XCTAssertEqual(usageWindowLabel(durationMins: 60, legacyLabel: "主窗口"), "1小时")
        XCTAssertEqual(usageWindowLabel(durationMins: 90, legacyLabel: "主窗口"), "90分")
        XCTAssertEqual(usageWindowLabel(durationMins: nil, legacyLabel: "主窗口"), "主窗口")
    }

    func testHelpKeepsTheFullRemainingDurationBeyondTheInlinePrecision() {
        let value = QuotaResetPresentation.make(
            resetAt: instant("2026-10-22T16:31:45Z"),
            now: instant("2026-10-20T08:10:00Z"), isCached: true)
        XCTAssertTrue(value.help.contains("还剩2天8小时21分45秒"))
        XCTAssertEqual(value.accessibilityLabel, value.help)
        XCTAssertTrue(value.help.contains("额度来自缓存"))

        let lessThanAMinute = QuotaResetPresentation.make(
            resetAt: instant("2026-10-20T08:10:45Z"),
            now: instant("2026-10-20T08:10:00Z"), isCached: false)
        XCTAssertTrue(lessThanAMinute.help.contains("还剩45秒"))
    }

    func testDateBoundariesClockChangesAndNewServerReportsRemainUnambiguous() {
        let cases: [(String, String, String, String)] = [
            ("2026-10-31T15:59:00Z", "2026-10-31T16:01:00Z", "明日00:01 重置 · 余2分", "明日00:01·2分"),
            ("2026-12-31T15:59:00Z", "2026-12-31T16:01:00Z", "明日00:01 重置 · 余2分", "明日00:01·2分"),
            ("2026-03-08T07:00:00Z", "2026-03-08T08:00:00Z", "16:00 重置 · 余1时0分", "16:00·1时0分"),
            ("2026-11-01T06:00:00Z", "2026-11-01T07:00:00Z", "15:00 重置 · 余1时0分", "15:00·1时0分"),
        ]
        for (now, resetAt, text, compact) in cases {
            let value = QuotaResetPresentation.make(resetAt: instant(resetAt), now: instant(now), isCached: false)
            XCTAssertEqual(value.text, text)
            XCTAssertEqual(value.compactText, compact)
        }

        let now = instant("2026-10-20T15:59:00Z")
        let reset = instant("2026-10-20T16:01:00Z")
        XCTAssertEqual(QuotaResetPresentation.make(resetAt: reset, now: now.addingTimeInterval(60), isCached: false).text,
                       "00:01 重置 · 余1分")
        XCTAssertEqual(QuotaResetPresentation.make(resetAt: reset.addingTimeInterval(120), now: now, isCached: false).text,
                       "明日00:03 重置 · 余4分")
        let expired = QuotaResetPresentation.make(resetAt: now.addingTimeInterval(-86_400), now: now, isCached: true)
        XCTAssertEqual(expired.compactText, "缓存10/19 23:59·待刷新")
    }

    func testEveryInlineStateHasAnUntruncatedCandidateAtTheProductionFontSize() {
        let now = instant("2026-10-20T08:10:00Z")
        let resets: [Date?] = [nil, now, now.addingTimeInterval(-86_400),
            now.addingTimeInterval(45), now.addingTimeInterval(15_660),
            instant("2026-10-20T16:31:00Z"), instant("2026-11-20T16:31:00Z"),
            instant("2027-11-20T16:31:00Z")]
        let font = NSFont.monospacedSystemFont(ofSize: 9, weight: .regular)
        for isCached in [false, true] {
            for resetAt in resets {
                let value = QuotaResetPresentation.make(resetAt: resetAt, now: now, isCached: isCached)
                let widths = [value.text, value.compactText].map {
                    NSString(string: $0).size(withAttributes: [.font: font]).width
                }
                XCTAssertLessThanOrEqual(widths.min() ?? .infinity, AccountMenuLayout.primaryTimeWidth, value.compactText)
                XCTAssertFalse(value.compactText.contains("…"))
                if isCached { XCTAssertTrue(value.compactText.contains("缓存")) }
            }
        }
    }
}
