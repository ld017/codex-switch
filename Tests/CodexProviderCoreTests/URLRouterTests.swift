import Foundation
import XCTest
@testable import CodexProviderCore

final class URLRouterTests: XCTestCase {
    func testRoutesExactActions() {
        XCTAssertEqual(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/openai")!
            ),
            .switchProvider(.openai)
        )
        XCTAssertEqual(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/sub2api")!
            ),
            .switchProvider(.sub2api)
        )
        XCTAssertEqual(
            URLRouter.action(
                from: URL(
                    string: "codex-switcher://shared-history/refresh"
                )!
            ),
            .refreshSharedHistory
        )
    }

    func testRejectsUnexpectedURLs() {
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "https://example.com/switch/openai")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/unknown")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://other/openai")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/open/ai")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/openai?again=1")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch/openai#fragment")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://user@switch/openai")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(string: "codex-switcher://switch:80/openai")!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(
                    string: "codex-switcher://shared-history/refresh/again"
                )!
            )
        )
        XCTAssertNil(
            URLRouter.action(
                from: URL(
                    string: "codex-switcher://shared-history/refresh?again=1"
                )!
            )
        )
    }
}
