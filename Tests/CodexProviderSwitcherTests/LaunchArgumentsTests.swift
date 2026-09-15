import XCTest
@testable import CodexProviderSwitcher

final class LaunchArgumentsTests: XCTestCase {
    func testLoginItemIsEnabledOnlyForInstallerLaunchArgument() {
        XCTAssertFalse(
            LaunchArguments(arguments: ["CodexProviderSwitcher"])
                .enableLoginItem
        )
        XCTAssertTrue(
            LaunchArguments(
                arguments: [
                    "CodexProviderSwitcher",
                    "--enable-login-item",
                ]
            ).enableLoginItem
        )
    }
}
