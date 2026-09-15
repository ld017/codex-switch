import Foundation

@MainActor
protocol SharedHistorySystem: AnyObject {
    func prepareSharedHistory() async throws
    func terminateCodex() async -> Bool
    func forceTerminateCodex() async -> Bool
    func launchCodex() async throws
}

extension CodexSystem: SharedHistorySystem {
    func prepareSharedHistory() async throws {
        try await prepareSharedHistoryLaunch()
    }
}

enum SharedHistoryRestartResult: Equatable {
    case restarted
    case cancelled
}

enum SharedHistoryRestartCoordinatorError: LocalizedError {
    case terminationFailed

    var errorDescription: String? {
        switch self {
        case .terminationFailed:
            return "Codex 未能退出，无法重新启动共享历史。"
        }
    }
}

@MainActor
final class SharedHistoryRestartCoordinator {
    private let system: any SharedHistorySystem

    init(system: any SharedHistorySystem) {
        self.system = system
    }

    func execute(
        confirmForceQuit: () -> Bool
    ) async throws -> SharedHistoryRestartResult {
        try await system.prepareSharedHistory()

        if !(await system.terminateCodex()) {
            guard confirmForceQuit() else {
                return .cancelled
            }
            guard await system.forceTerminateCodex() else {
                throw SharedHistoryRestartCoordinatorError.terminationFailed
            }
        }

        try await system.launchCodex()
        return .restarted
    }
}
