import CodexProviderCore
import Foundation

@MainActor
protocol SwitchSystem: AnyObject {
    func terminateCodex() async -> Bool
    func forceTerminateCodex() async -> Bool
    func prepareConfigurationSwitch(to target: Provider) async throws
        -> SwitchContext
    func restore(_ context: SwitchContext) async throws
    func launchCodex() async throws
}

extension CodexSystem: SwitchSystem {
    func terminateCodex() async -> Bool {
        await terminateCodex(timeout: 15)
    }

    func forceTerminateCodex() async -> Bool {
        await forceTerminateCodex(timeout: 8)
    }

    func launchCodex() async throws {
        try await launchCodex(timeout: 20)
    }
}

enum SwitchCoordinatorResult: Equatable {
    case switched
    case cancelled
}

enum SwitchCoordinatorError: LocalizedError {
    case terminationFailed
    case recoveryFailed

    var errorDescription: String? {
        switch self {
        case .terminationFailed:
            return "Codex 未能退出，配置没有更改。"
        case .recoveryFailed:
            return "切换失败，并且无法完整恢复原配置或重新打开 Codex。"
        }
    }
}

@MainActor
final class SwitchCoordinator {
    private let system: any SwitchSystem

    init(system: any SwitchSystem) {
        self.system = system
    }

    func execute(
        target: Provider,
        confirmForceQuit: () -> Bool
    ) async throws -> SwitchCoordinatorResult {
        if !(await system.terminateCodex()) {
            guard confirmForceQuit() else {
                return .cancelled
            }
            guard await system.forceTerminateCodex() else {
                throw StagedSwitchError(
                    stage: .codexTermination,
                    underlying: SwitchCoordinatorError.terminationFailed
                )
            }
        }

        let context: SwitchContext
        do {
            context = try await system.prepareConfigurationSwitch(to: target)
        } catch {
            let configurationError = StagedSwitchError.wrapping(
                error,
                stage: .configuration
            )
            do {
                try await system.launchCodex()
            } catch let recoveryError {
                throw StagedSwitchError(
                    stage: .recovery,
                    underlying: recoveryError
                )
            }
            throw configurationError
        }

        do {
            try await system.launchCodex()
            return .switched
        } catch let launchError {
            do {
                try await system.restore(context)
                try await system.launchCodex()
            } catch let recoveryError {
                throw StagedSwitchError(
                    stage: .recovery,
                    underlying: recoveryError
                )
            }
            throw StagedSwitchError.wrapping(
                launchError,
                stage: .codexLaunch
            )
        }
    }
}

@MainActor
final class SwitchActivity {
    private(set) var isActive = false

    @discardableResult
    func begin() -> Bool {
        guard !isActive else {
            return false
        }
        isActive = true
        return true
    }

    func finish() {
        isActive = false
    }
}
