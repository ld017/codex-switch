import XCTest
@testable import CodexProviderCore

final class SwitchDecisionTests: XCTestCase {
    func testSkipsMatchingProvider() {
        XCTAssertFalse(
            SwitchDecision.requiresSwitch(from: .sub2api, to: .sub2api)
        )
        XCTAssertFalse(
            SwitchDecision.requiresSwitch(from: .openai, to: .openai)
        )
    }

    func testRequiresDifferentProvider() {
        XCTAssertTrue(
            SwitchDecision.requiresSwitch(from: .sub2api, to: .openai)
        )
        XCTAssertTrue(
            SwitchDecision.requiresSwitch(from: .openai, to: .sub2api)
        )
    }
}
