import Foundation
import XCTest
@testable import CodexProviderSwitcher

final class ResetCreditPresentationTests: XCTestCase {
    func testDisplaysCountAndNearestExpirationInLocalTime() {
        let text = ResetCreditPresentation.text(
            count: 3,
            nearestExpiration: Date(timeIntervalSince1970: 1_789_944_360),
            timeZone: TimeZone(identifier: "Asia/Shanghai")!)

        XCTAssertEqual(text, "重置卡 3 张 · 最近到期 9月21日 06:46")
    }

    func testDisplaysZeroWhenTheAccountHasNoAvailableResetCards() {
        XCTAssertEqual(
            ResetCreditPresentation.text(count: 0, nearestExpiration: nil),
            "重置卡 0 张")
    }
}
