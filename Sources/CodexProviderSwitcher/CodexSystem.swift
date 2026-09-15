import AppKit
import CodexProviderCore
import Darwin
import Foundation

struct SwitchContext {
    let previousProvider: Provider
    let backupURL: URL
}

enum CodexSystemError: LocalizedError {
    case configMissing
    case sub2APIConfigurationMissing
    case sub2APIUnavailable
    case sub2APICredentialMissing
    case chatGPTLoginMissing
    case configValidationFailed
    case configWriteFailed
    case codexLaunchFailed
    case recoveryFailed
    case sharedHistoryProxyMissing
    case sharedHistoryLaunchEnvironmentFailed
    case proxyEnvironmentConflict(variables: [String])
    case clashProxyUnavailable(status: ClashReadinessStatus)

    var errorDescription: String? {
        switch self {
        case .configMissing:
            return "没有找到 Codex 配置文件。"
        case .sub2APIConfigurationMissing:
            return "共享配置中缺少完整的 Sub2API 定义。"
        case .sub2APIUnavailable:
            return "Sub2API 本机服务未在 127.0.0.1:8080 响应。"
        case .sub2APICredentialMissing:
            return "系统钥匙串中没有找到 Sub2API 凭据。"
        case .chatGPTLoginMissing:
            return "没有找到有效的 ChatGPT 登录状态。"
        case .configValidationFailed:
            return "Codex 无法验证修改后的配置，原配置已恢复。"
        case .configWriteFailed:
            return "无法安全写入 Codex 配置。"
        case .codexLaunchFailed:
            return "配置已切换，但 Codex 没有成功重新打开。"
        case .recoveryFailed:
            return "切换失败，并且无法验证原配置已恢复。"
        case .sharedHistoryProxyMissing:
            return "没有找到可执行的共享历史代理程序。"
        case .sharedHistoryLaunchEnvironmentFailed:
            return "无法为 Codex 配置共享历史启动环境。"
        case .proxyEnvironmentConflict(let variables):
            return "检测到可能造成 Clash TUN 自循环的全局代理变量："
                + variables.joined(separator: "、")
                + "。请先清除这些 launchctl 变量。"
        case .clashProxyUnavailable(let status):
            return "Clash 代理尚未就绪（\(status.userDescription)）。"
                + "请在 Clash Verge 开启系统代理和虚拟网卡后重试。"
        }
    }
}

final class CodexSystem {
    static let chatGPTBundleIdentifier = "com.openai.codex"

    private let fileManager = FileManager.default
    private let homeDirectory = FileManager.default.homeDirectoryForCurrentUser
    private let sharedHistoryProxyURL: URL?

    init(sharedHistoryProxyURL: URL? = nil) {
        self.sharedHistoryProxyURL = sharedHistoryProxyURL
    }

    private var codexHome: URL {
        homeDirectory.appendingPathComponent(".codex", isDirectory: true)
    }

    private var configURL: URL {
        codexHome.appendingPathComponent("config.toml")
    }

    private var authURL: URL {
        codexHome.appendingPathComponent("auth.json")
    }

    private var backupManager: BackupManager {
        BackupManager(
            backupDirectory: codexHome
                .appendingPathComponent(
                    "provider-switcher",
                    isDirectory: true
                )
                .appendingPathComponent("backups", isDirectory: true),
            retentionLimit: 10
        )
    }

    private let codexBinary = URL(
        fileURLWithPath:
            "/Applications/ChatGPT.app/Contents/Resources/codex"
    )
    private let chatGPTApplication = URL(
        fileURLWithPath: "/Applications/ChatGPT.app",
        isDirectory: true
    )

    func currentProvider() throws -> Provider {
        let source = try readConfiguration()
        return try ConfigEditor.currentProvider(in: source)
    }

    func sub2APIIsOnline() async -> Bool {
        await LocalSub2APIHealthProbe().isOnline()
    }

    func proxyEnvironmentConflicts() async -> [ProxyEnvironmentConflict] {
        await Task.detached {
            ProxyEnvironmentGuard().conflicts()
        }.value
    }

    func clashReadinessStatus() async -> ClashReadinessStatus {
        await Task.detached {
            ClashReadinessGuard().status()
        }.value
    }

    func validateProxyEnvironment() async throws {
        let conflicts = await proxyEnvironmentConflicts()
        guard conflicts.isEmpty else {
            throw CodexSystemError.proxyEnvironmentConflict(
                variables: conflicts.map(\.variable)
            )
        }
        let readiness = await clashReadinessStatus()
        guard readiness == .ready else {
            throw CodexSystemError.clashProxyUnavailable(status: readiness)
        }
    }

    func ensureSub2APIAvailable() async throws {
        try await Sub2APIServiceController().ensureAvailable()
    }

    func validateTarget(_ target: Provider) async throws {
        try await Task.detached { [self] in
            switch target {
            case .openai:
                guard fileManager.fileExists(atPath: authURL.path),
                      let data = try? Data(contentsOf: authURL),
                      (try? AuthInspector.authMode(from: data)) == "chatgpt"
                else {
                    throw CodexSystemError.chatGPTLoginMissing
                }
            case .sub2api:
                let source = try readConfiguration()
                guard ConfigEditor.hasSub2APIConfiguration(in: source) else {
                    throw CodexSystemError.sub2APIConfigurationMissing
                }
                let portResult = try? ProcessRunner.run(
                    executable: URL(fileURLWithPath: "/usr/bin/nc"),
                    arguments: [
                        "-z", "-w", "2", "127.0.0.1", "8080",
                    ],
                    captureOutput: false
                )
                guard portResult?.exitCode == 0 else {
                    throw CodexSystemError.sub2APIUnavailable
                }
                let keychainResult = try? ProcessRunner.run(
                    executable: URL(fileURLWithPath: "/usr/bin/security"),
                    arguments: [
                        "find-generic-password",
                        "-a", NSUserName(),
                        "-s", "sub2api-codex-local",
                    ],
                    captureOutput: false
                )
                guard keychainResult?.exitCode == 0 else {
                    throw CodexSystemError.sub2APICredentialMissing
                }
            }
        }.value
    }

    func prepareConfigurationSwitch(
        to target: Provider
    ) async throws -> SwitchContext {
        try await Task.detached { [self] in
            let source = try readConfiguration()
            let previousProvider = try ConfigEditor.currentProvider(in: source)
            if target == .sub2api,
               !ConfigEditor.hasSub2APIConfiguration(in: source) {
                throw CodexSystemError.sub2APIConfigurationMissing
            }

            let backupURL = try backupManager.createBackup(of: configURL)
            do {
                let updated = try ConfigEditor.replacingProvider(
                    in: source,
                    with: target
                )
                try atomicallyWriteConfiguration(updated)
                let doctor = try ProcessRunner.run(
                    executable: codexBinary,
                    arguments: ["doctor", "--json"],
                    captureOutput: true
                )
                let result = DoctorResult(
                    exitCode: doctor.exitCode,
                    standardOutput: doctor.standardOutput
                )
                guard result.isConfigurationValid else {
                    throw CodexSystemError.configValidationFailed
                }
            } catch {
                do {
                    try restoreAndVerify(backupURL)
                } catch {
                    throw CodexSystemError.recoveryFailed
                }
                throw error
            }

            return SwitchContext(
                previousProvider: previousProvider,
                backupURL: backupURL
            )
        }.value
    }

    func restore(_ context: SwitchContext) async throws {
        try await Task.detached { [self] in
            try restoreAndVerify(context.backupURL)
            let restoredSource = try readConfiguration()
            guard try ConfigEditor.currentProvider(in: restoredSource)
                == context.previousProvider else {
                throw CodexSystemError.recoveryFailed
            }
        }.value
    }

    func prepareSharedHistoryLaunch() async throws {
        try await Task.detached { [self] in
            let proxyURL = try packagedSharedHistoryProxyURL()
            do {
                let setEnvironment = try ProcessRunner.run(
                    executable: URL(fileURLWithPath: "/bin/launchctl"),
                    arguments: ["setenv", "CODEX_CLI_PATH", proxyURL.path],
                    captureOutput: false
                )
                guard setEnvironment.exitCode == 0 else {
                    throw CodexSystemError.sharedHistoryLaunchEnvironmentFailed
                }
                let configuredPath = try ProcessRunner.run(
                    executable: URL(fileURLWithPath: "/bin/launchctl"),
                    arguments: ["getenv", "CODEX_CLI_PATH"],
                    captureOutput: true
                )
                guard configuredPath.exitCode == 0,
                      configuredPath.standardOutput
                        .trimmingCharacters(in: .whitespacesAndNewlines)
                        == proxyURL.path else {
                    throw CodexSystemError.sharedHistoryLaunchEnvironmentFailed
                }
                guard Darwin.setenv(
                    "CODEX_CLI_PATH",
                    proxyURL.path,
                    1
                ) == 0,
                let currentPath = Darwin.getenv("CODEX_CLI_PATH"),
                String(cString: currentPath)
                    == proxyURL.path else {
                    throw CodexSystemError.sharedHistoryLaunchEnvironmentFailed
                }
            } catch let error as CodexSystemError {
                throw error
            } catch {
                throw CodexSystemError.sharedHistoryLaunchEnvironmentFailed
            }
        }.value
    }

    func sharedHistoryIsConfigured() async -> Bool {
        await Task.detached { [self] in
            guard let proxyURL = try? packagedSharedHistoryProxyURL(),
                  let configuredPath = try? ProcessRunner.run(
                    executable: URL(fileURLWithPath: "/bin/launchctl"),
                    arguments: ["getenv", "CODEX_CLI_PATH"],
                    captureOutput: true
                  ),
                  configuredPath.exitCode == 0 else {
                return false
            }
            let launchEnvironmentMatches = configuredPath.standardOutput
                .trimmingCharacters(in: .whitespacesAndNewlines)
                == proxyURL.path
            let processEnvironmentMatches = Darwin.getenv(
                "CODEX_CLI_PATH"
            ).map { String(cString: $0) == proxyURL.path } ?? false
            return launchEnvironmentMatches && processEnvironmentMatches
        }.value
    }

    @MainActor
    func terminateCodex(timeout: TimeInterval = 15) async -> Bool {
        let applications = NSRunningApplication.runningApplications(
            withBundleIdentifier: Self.chatGPTBundleIdentifier
        )
        guard !applications.isEmpty else {
            return true
        }
        applications.forEach { _ = $0.terminate() }
        return await waitForCodex(running: false, timeout: timeout)
    }

    @MainActor
    func forceTerminateCodex(timeout: TimeInterval = 8) async -> Bool {
        await CodexProcessTerminator().forceTerminateCodex()
    }

    @MainActor
    func launchCodex(timeout: TimeInterval = 20) async throws {
        let configuration = NSWorkspace.OpenConfiguration()
        configuration.activates = true
        try await withCheckedThrowingContinuation {
            (continuation: CheckedContinuation<Void, Error>) in
            NSWorkspace.shared.openApplication(
                at: chatGPTApplication,
                configuration: configuration
            ) { _, error in
                if let error {
                    continuation.resume(throwing: error)
                } else {
                    continuation.resume()
                }
            }
        }
        guard await waitForCodex(running: true, timeout: timeout) else {
            throw CodexSystemError.codexLaunchFailed
        }
    }

    @MainActor
    func openCodex() {
        NSWorkspace.shared.openApplication(
            at: chatGPTApplication,
            configuration: NSWorkspace.OpenConfiguration()
        )
    }

    @MainActor
    private func waitForCodex(
        running expectedState: Bool,
        timeout: TimeInterval
    ) async -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            let isRunning = !NSRunningApplication.runningApplications(
                withBundleIdentifier: Self.chatGPTBundleIdentifier
            ).isEmpty
            if isRunning == expectedState {
                return true
            }
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
        return false
    }

    private func readConfiguration() throws -> String {
        guard fileManager.fileExists(atPath: configURL.path) else {
            throw CodexSystemError.configMissing
        }
        return try String(contentsOf: configURL, encoding: .utf8)
    }

    private func atomicallyWriteConfiguration(_ value: String) throws {
        guard let data = value.data(using: .utf8) else {
            throw CodexSystemError.configWriteFailed
        }
        do {
            try ConfigurationFileWriter.replace(
                contents: data,
                at: configURL
            )
        } catch {
            throw CodexSystemError.configWriteFailed
        }
    }

    private func restoreAndVerify(_ backupURL: URL) throws {
        do {
            let expected = try Data(contentsOf: backupURL)
            try backupManager.restoreBackup(backupURL, to: configURL)
            let restored = try Data(contentsOf: configURL)
            guard restored == expected else {
                throw CodexSystemError.recoveryFailed
            }
        } catch {
            throw CodexSystemError.recoveryFailed
        }
    }

    private func packagedSharedHistoryProxyURL() throws -> URL {
        let proxyURL = sharedHistoryProxyURL ?? Bundle.main.bundleURL
                .appendingPathComponent("Contents", isDirectory: true)
                .appendingPathComponent("MacOS", isDirectory: true)
                .appendingPathComponent("CodexSharedHistoryProxy")
        let values = try? proxyURL.resourceValues(
            forKeys: [.isRegularFileKey]
        )
        guard values?.isRegularFile == true,
              fileManager.isExecutableFile(atPath: proxyURL.path) else {
            throw CodexSystemError.sharedHistoryProxyMissing
        }
        return proxyURL
    }
}
