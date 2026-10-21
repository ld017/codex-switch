import XCTest
import CodexProfileCore
@testable import CodexProviderSwitcher

final class TokenUsagePresentationTests: XCTestCase {
    func testDeduplicatesIdentityUsingNewestDataAndExcludesUnknownIdentity() {
        let older = TokenActivity(lifetimeTokens: 10000, dailyUsageBuckets: nil, fetchedAt: Date(timeIntervalSince1970: 1))
        let newer = TokenActivity(lifetimeTokens: 20000, dailyUsageBuckets: nil, fetchedAt: Date(timeIntervalSince1970: 2))
        let result = TokenUsagePresentation.aggregate([
            .init(identity: "same", activity: older, cached: false),
            .init(identity: "same", activity: newer, cached: true),
            .init(identity: "missing", activity: nil, cached: false),
            .init(identity: nil, activity: newer, cached: false),
        ])
        XCTAssertEqual(result, "Token 合计：2.00 万 · 1 账号 · 2 未计入 · 含缓存")
    }

    func testMissingAndZeroAreDistinct() {
        XCTAssertEqual(TokenUsagePresentation.aggregate([]), "Token 合计：未获取")
        XCTAssertEqual(TokenUsagePresentation.aggregate([
            .init(identity: "zero", activity: TokenActivity(lifetimeTokens: 0, dailyUsageBuckets: nil), cached: false)
        ]), "Token 合计：0 · 1 账号")
        XCTAssertEqual(TokenUsagePresentation.cardLabel(.notSetUp), "累计 Token：未获取 · 本月：未获取")
    }

    private func activity(_ buckets: String, total: Int64 = 100_000_000) throws -> TokenActivity {
        let days = try JSONDecoder().decode([TokenActivity.Day].self, from: Data(buckets.utf8))
        return TokenActivity(lifetimeTokens: total, dailyUsageBuckets: days)
    }

    func testMonthlyBoundariesDuplicatesAndMissingDays() throws {
        let now = ISO8601DateFormatter().date(from: "2026-10-03T04:00:00Z")!
        let data = try activity("""
        [{"startDate":"2026-09-30","tokens":9000000},
         {"startDate":"2026-10-02","tokens":6907839},
         {"startDate":"2026-10-02","tokens":6907839},
         {"startDate":"2026-10-04","tokens":1000000},
         {"startDate":"2026-10-00","tokens":1000000}]
        """)
        let month = TokenUsagePresentation.monthUsage(data, now: now)
        XCTAssertEqual(month?.tokens, 6907839)
        XCTAssertEqual(month?.through, "2026-10-02")
        let old = try activity("[{\"startDate\":\"2026-09-30\",\"tokens\":100}]")
        XCTAssertNil(TokenUsagePresentation.monthUsage(old, now: now))
        let summary = TokenUsagePresentation.summary([
            .init(identity: "a", activity: data, cached: false),
            .init(identity: "a", activity: data, cached: false),
            .init(identity: "b", activity: old, cached: true)
        ], now: now)
        XCTAssertEqual(summary.values, "累计：2.00 亿 · 本月：0.07 亿（部分）")
        XCTAssertEqual(summary.coverage, "账号覆盖：累计 2/2 · 本月 1/2 · 含缓存")
    }

    func testUnitFormattingKeepsSmallPositiveValuesDistinctFromZero() {
        XCTAssertEqual(TokenUsagePresentation.millions(6907839), "6.91 百万")
        XCTAssertEqual(TokenUsagePresentation.hundredMillions(6907839), "0.07 亿")
        XCTAssertEqual(TokenUsagePresentation.hundredMillions(1), "<0.01 亿")
        XCTAssertEqual(TokenUsagePresentation.millions(1), "<0.01 百万")
        XCTAssertEqual(TokenUsagePresentation.hundredMillions(0), "0.00 亿")
    }

    func testMonthUsesBeijingCalendarAcrossYearBoundary() throws {
        let before = ISO8601DateFormatter().date(from: "2026-12-31T15:59:59Z")!
        let data = try activity("[{\"startDate\":\"2027-01-01\",\"tokens\":1000000}]")
        XCTAssertNil(TokenUsagePresentation.monthUsage(data, now: before))
        XCTAssertEqual(TokenUsagePresentation.monthUsage(data, now: before.addingTimeInterval(1))?.tokens, 1000000)
    }
}
