import CodexProviderCore
import Darwin
import Foundation

enum LoginItemError: LocalizedError, Equatable {
    case registrationFailed
    case cleanupFailed

    var errorDescription: String? {
        switch self {
        case .registrationFailed:
            return "无法更新登录启动项。"
        case .cleanupFailed:
            return "登录启动项注册失败，并且无法清理残留文件。"
        }
    }
}

final class LoginItemManager {
    static let label = "com.lindui017.codex-provider-switcher"

    private let fileManager = FileManager.default
    private let homeDirectory: URL
    private let launchctl: ([String]) -> Int32

    init(
        homeDirectory: URL = FileManager.default.homeDirectoryForCurrentUser,
        runLaunchctl: @escaping ([String]) -> Int32 = {
            LoginItemManager.runLaunchctlProcess($0)
        }
    ) {
        self.homeDirectory = homeDirectory
        launchctl = runLaunchctl
    }

    var launchAgentURL: URL {
        homeDirectory
            .appendingPathComponent("Library/LaunchAgents", isDirectory: true)
            .appendingPathComponent("\(Self.label).plist")
    }

    private var appURL: URL {
        homeDirectory
            .appendingPathComponent("Applications", isDirectory: true)
            .appendingPathComponent(
                "Codex Switch.app",
                isDirectory: true
            )
    }

    var isEnabled: Bool {
        launchctl([
            "print",
            "gui/\(getuid())/\(Self.label)",
        ]) == 0
    }

    func enable() throws {
        try fileManager.createDirectory(
            at: launchAgentURL.deletingLastPathComponent(),
            withIntermediateDirectories: true
        )
        let propertyList: [String: Any] = [
            "Label": Self.label,
            "ProgramArguments": [
                "/usr/bin/open",
                "-g",
                appURL.path,
            ],
            "RunAtLoad": true,
        ]
        let data = try PropertyListSerialization.data(
            fromPropertyList: propertyList,
            format: .xml,
            options: 0
        )
        try data.write(to: launchAgentURL, options: [.atomic])
        try fileManager.setAttributes(
            [.posixPermissions: NSNumber(value: 0o644)],
            ofItemAtPath: launchAgentURL.path
        )

        _ = launchctl([
            "bootout",
            "gui/\(getuid())/\(Self.label)",
        ])
        guard launchctl([
            "bootstrap",
            "gui/\(getuid())",
            launchAgentURL.path,
        ]) == 0, isEnabled else {
            _ = launchctl([
                "bootout",
                "gui/\(getuid())/\(Self.label)",
            ])
            do {
                if fileManager.fileExists(atPath: launchAgentURL.path) {
                    try fileManager.removeItem(at: launchAgentURL)
                }
                guard !fileManager.fileExists(
                    atPath: launchAgentURL.path
                ) else {
                    throw LoginItemError.cleanupFailed
                }
            } catch {
                throw LoginItemError.cleanupFailed
            }
            throw LoginItemError.registrationFailed
        }
    }

    func disable() throws {
        _ = launchctl([
            "bootout",
            "gui/\(getuid())/\(Self.label)",
        ])
        if fileManager.fileExists(atPath: launchAgentURL.path) {
            try fileManager.removeItem(at: launchAgentURL)
        }
    }

    private static func runLaunchctlProcess(_ arguments: [String]) -> Int32 {
        do {
            return try ProcessRunner.run(
                executable: URL(fileURLWithPath: "/bin/launchctl"),
                arguments: arguments,
                captureOutput: false,
                timeout: 5
            ).exitCode
        } catch {
            return -1
        }
    }
}
