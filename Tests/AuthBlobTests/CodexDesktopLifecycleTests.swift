import Foundation
import XCTest
@testable import CodexProfileCore

final class CodexDesktopLifecycleTests: XCTestCase {
    func testResolvesCurrentBundledExecutable() throws {
        try withApp(cli: "Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex") { app, cli in
            XCTAssertEqual(try CodexDesktopLifecycle(environment: ["CODEX_APP": app.path]).resolveBundledCLI(), cli.path)
        }
    }

    func testResolvesCurrentCLIOverrideToOuterDesktopApp() throws {
        try withApp(cli: "Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex") { _, cli in
            XCTAssertEqual(try CodexDesktopLifecycle(environment: ["CODEX_BUNDLED_CLI": cli.path]).resolveBundledCLI(), cli.path)
        }
    }

    func testRetainsLegacyLayoutSupport() throws {
        try withApp(cli: "Contents/Resources/codex") { app, cli in
            XCTAssertEqual(try CodexDesktopLifecycle(environment: ["CODEX_APP": app.path]).resolveBundledCLI(), cli.path)
        }
    }

    func testResolvesLauncherLayout() throws {
        try withApp(cli: "Contents/Resources/codex-cli/bin/codex") { app, cli in
            XCTAssertEqual(try CodexDesktopLifecycle(environment: ["CODEX_APP": app.path]).resolveBundledCLI(), cli.path)
        }
    }

    func testRejectsOverrideFromDifferentApp() throws {
        try withApp(cli: "Contents/Resources/codex") { app, _ in
            try withApp(cli: "Contents/Resources/codex") { _, otherCLI in
                XCTAssertThrowsError(try CodexDesktopLifecycle(environment: [
                    "CODEX_APP": app.path, "CODEX_BUNDLED_CLI": otherCLI.path,
                ]).resolveBundledCLI())
            }
        }
    }

    func testMissingCLIStillFails() throws {
        try withApp(cli: "Contents/Resources/codex") { app, cli in
            try FileManager.default.removeItem(at: cli)
            XCTAssertThrowsError(try CodexDesktopLifecycle(environment: ["CODEX_APP": app.path]).resolveBundledCLI())
        }
    }

    private func withApp(cli relativePath: String, body: (URL, URL) throws -> Void) throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let app = root.appendingPathComponent("ChatGPT.app")
        defer { try? FileManager.default.removeItem(at: root) }
        let cli = app.appendingPathComponent(relativePath)
        for file in [app.appendingPathComponent("Contents/MacOS/ChatGPT"), cli] {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            try Data("#!/bin/sh\nexit 0\n".utf8).write(to: file)
            try FileManager.default.setAttributes([.posixPermissions: 0o755], ofItemAtPath: file.path)
        }
        let info: [String: Any] = ["CFBundleIdentifier": "com.openai.codex", "CFBundleExecutable": "ChatGPT"]
        try PropertyListSerialization.data(fromPropertyList: info, format: .xml, options: 0)
            .write(to: app.appendingPathComponent("Contents/Info.plist"))
        try body(app.resolvingSymlinksInPath(), cli.resolvingSymlinksInPath())
    }
}
