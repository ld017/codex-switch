import CodexProviderCore
import Foundation

protocol ContainerCommandRunning {
    func run(arguments: [String]) async throws -> ProcessResult
}

struct AppleContainerCommandRunner: ContainerCommandRunning {
    let executable = URL(fileURLWithPath: "/usr/local/bin/container")

    func run(arguments: [String]) async throws -> ProcessResult {
        try await Task.detached {
            try ProcessRunner.run(
                executable: executable,
                arguments: arguments,
                captureOutput: true,
                timeout: 75
            )
        }.value
    }
}

protocol Sub2APIHealthProbing {
    func isOnline() async -> Bool
}

protocol Sub2APIStackRunning {
    func runUp() async throws -> ProcessResult
}

struct LocalSub2APIStackRunner: Sub2APIStackRunning {
    private var helperURL: URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".local/bin/sub2api-apple-up")
    }

    func runUp() async throws -> ProcessResult {
        try await Task.detached {
            try ProcessRunner.run(
                executable: helperURL,
                arguments: [],
                captureOutput: true,
                timeout: 180
            )
        }.value
    }
}

struct LocalSub2APIHealthProbe: Sub2APIHealthProbing {
    func isOnline() async -> Bool {
        await Task.detached {
            let result = try? ProcessRunner.run(
                executable: URL(fileURLWithPath: "/usr/bin/nc"),
                arguments: ["-z", "-w", "2", "127.0.0.1", "8080"],
                captureOutput: false,
                timeout: 3
            )
            return result?.exitCode == 0
        }.value
    }
}

enum Sub2APIServiceControllerError: Error, Equatable, LocalizedError {
    case systemStatusFailed
    case systemStartFailed(exitCode: Int32)
    case stackStartFailed(exitCode: Int32)
    case healthCheckTimedOut

    var errorDescription: String? {
        switch self {
        case .systemStatusFailed:
            return "无法检查 Apple Container 系统状态。"
        case .systemStartFailed(let exitCode):
            return "Apple Container 系统启动失败（退出码 \(exitCode)）。"
        case .stackStartFailed(let exitCode):
            return "Sub2API 服务栈恢复失败（退出码 \(exitCode)）。"
        case .healthCheckTimedOut:
            return "Sub2API 已启动，但 127.0.0.1:8080 未在限定时间内就绪。"
        }
    }
}

struct Sub2APIServiceController {
    let runner: any ContainerCommandRunning
    let stackRunner: any Sub2APIStackRunning
    let healthProbe: any Sub2APIHealthProbing
    let healthAttempts: Int
    let waitBetweenHealthChecks: () async -> Void

    init(
        runner: any ContainerCommandRunning = AppleContainerCommandRunner(),
        stackRunner: any Sub2APIStackRunning = LocalSub2APIStackRunner(),
        healthProbe: any Sub2APIHealthProbing = LocalSub2APIHealthProbe(),
        healthAttempts: Int = 120,
        waitBetweenHealthChecks: @escaping () async -> Void = {
            try? await Task.sleep(nanoseconds: 500_000_000)
        }
    ) {
        self.runner = runner
        self.stackRunner = stackRunner
        self.healthProbe = healthProbe
        self.healthAttempts = healthAttempts
        self.waitBetweenHealthChecks = waitBetweenHealthChecks
    }

    func ensureAvailable() async throws {
        if await healthProbe.isOnline() {
            return
        }

        let status: ProcessResult
        do {
            status = try await runner.run(
                arguments: ["system", "status", "--format", "json"]
            )
        } catch {
            throw Sub2APIServiceControllerError.systemStatusFailed
        }
        if status.exitCode != 0 {
            let start = try await runner.run(arguments: [
                "system", "start", "--disable-kernel-install",
                "--timeout", "60",
            ])
            guard start.exitCode == 0 else {
                throw Sub2APIServiceControllerError.systemStartFailed(
                    exitCode: start.exitCode
                )
            }
        }

        let stack = try await stackRunner.runUp()
        guard stack.exitCode == 0 else {
            throw Sub2APIServiceControllerError.stackStartFailed(
                exitCode: stack.exitCode
            )
        }

        for attempt in 0..<max(healthAttempts, 1) {
            if await healthProbe.isOnline() {
                return
            }
            if attempt + 1 < max(healthAttempts, 1) {
                await waitBetweenHealthChecks()
            }
        }
        throw Sub2APIServiceControllerError.healthCheckTimedOut
    }
}
