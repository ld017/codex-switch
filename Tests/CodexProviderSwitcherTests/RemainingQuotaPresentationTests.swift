import XCTest
@testable import CodexProviderSwitcher

final class RemainingQuotaPresentationTests: XCTestCase {
    func testConvertsUsedPercentageToClampedRemainingPercentage() {
        XCTAssertEqual(RemainingQuotaPresentation.percent(fromUsed: 0), 100)
        XCTAssertEqual(RemainingQuotaPresentation.percent(fromUsed: 37), 63)
        XCTAssertEqual(RemainingQuotaPresentation.percent(fromUsed: 100), 0)
        XCTAssertEqual(RemainingQuotaPresentation.percent(fromUsed: -5), 100)
        XCTAssertEqual(RemainingQuotaPresentation.percent(fromUsed: 120), 0)
    }

    func testClassifiesLowRemainingQuotaAsUrgent() {
        XCTAssertEqual(RemainingQuotaPresentation.urgency(forRemaining: 31), .normal)
        XCTAssertEqual(RemainingQuotaPresentation.urgency(forRemaining: 30), .warning)
        XCTAssertEqual(RemainingQuotaPresentation.urgency(forRemaining: 11), .warning)
        XCTAssertEqual(RemainingQuotaPresentation.urgency(forRemaining: 10), .critical)
        XCTAssertEqual(RemainingQuotaPresentation.urgency(forRemaining: 0), .critical)
    }
}
