import Foundation
import XCTest
@testable import CodexProviderSwitcher

final class LoginItemManagerTests: XCTestCase {
    private var temporaryHome: URL!

    override func setUpWithError() throws {
        temporaryHome = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: temporaryHome,
            withIntermediateDirectories: true
        )
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporaryHome)
    }

    func testEnabledStateComesFromLaunchdNotPlistExistence() throws {
        let manager = LoginItemManager(
            homeDirectory: temporaryHome,
            runLaunchctl: { arguments in
                arguments.first == "print" ? 1 : 0
            }
        )
        try FileManager.default.createDirectory(
            at: manager.launchAgentURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        try Data("stale".utf8).write(to: manager.launchAgentURL)

        XCTAssertFalse(manager.isEnabled)
    }

    func testBootstrapFailureRemovesStalePlist() {
        let manager = LoginItemManager(
            homeDirectory: temporaryHome,
            runLaunchctl: { arguments in
                arguments.first == "bootstrap" ? 1 : 0
            }
        )

        XCTAssertThrowsError(try manager.enable())
        XCTAssertFalse(
            FileManager.default.fileExists(
                atPath: manager.launchAgentURL.path
            )
        )
    }

    func testBootstrapCleanupFailureIsSurfaced() throws {
        let launchAgentsURL = temporaryHome
            .appendingPathComponent("Library/LaunchAgents", isDirectory: true)
        let manager = LoginItemManager(
            homeDirectory: temporaryHome,
            runLaunchctl: { arguments in
                if arguments.first == "bootstrap" {
                    try? FileManager.default.setAttributes(
                        [.posixPermissions: NSNumber(value: 0o500)],
                        ofItemAtPath: launchAgentsURL.path
                    )
                    return 1
                }
                return 0
            }
        )
        defer {
            try? FileManager.default.setAttributes(
                [.posixPermissions: NSNumber(value: 0o700)],
                ofItemAtPath: launchAgentsURL.path
            )
        }

        XCTAssertThrowsError(try manager.enable()) { error in
            XCTAssertEqual(
                error as? LoginItemError,
                .cleanupFailed
            )
        }
    }
}
