import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class SharedHistoryRestartCoordinatorTests: XCTestCase {
    func testRestartPreparesSharedHistoryBeforeTerminatingAndLaunching() async throws {
        let system = FakeSharedHistorySystem()
        let coordinator = SharedHistoryRestartCoordinator(system: system)

        let result = try await coordinator.execute(confirmForceQuit: { false })

        XCTAssertEqual(result, .restarted)
        XCTAssertEqual(system.events, ["prepareSharedHistory", "terminate", "launch"])
    }

    func testPreparationFailureStopsBeforeTermination() async {
        let system = FakeSharedHistorySystem()
        system.prepareError = SharedHistoryTestError.prepare
        let coordinator = SharedHistoryRestartCoordinator(system: system)

        await XCTAssertThrowsErrorAsync(
            try await coordinator.execute(confirmForceQuit: { false })
        ) { error in
            XCTAssertEqual(error as? SharedHistoryTestError, .prepare)
        }
        XCTAssertEqual(system.events, ["prepareSharedHistory"])
    }

    func testDeclinedForceQuitCancelsWithoutLaunching() async throws {
        let system = FakeSharedHistorySystem()
        system.terminateResult = false
        let coordinator = SharedHistoryRestartCoordinator(system: system)

        let result = try await coordinator.execute(confirmForceQuit: { false })

        XCTAssertEqual(result, .cancelled)
        XCTAssertEqual(system.events, ["prepareSharedHistory", "terminate"])
    }

    func testApprovedForceQuitTerminatesBeforeLaunching() async throws {
        let system = FakeSharedHistorySystem()
        system.terminateResult = false
        let coordinator = SharedHistoryRestartCoordinator(system: system)

        let result = try await coordinator.execute(confirmForceQuit: { true })

        XCTAssertEqual(result, .restarted)
        XCTAssertEqual(
            system.events,
            ["prepareSharedHistory", "terminate", "forceTerminate", "launch"]
        )
    }
}

@MainActor
private final class FakeSharedHistorySystem: SharedHistorySystem {
    var events: [String] = []
    var terminateResult = true
    var forceTerminateResult = true
    var prepareError: Error?

    func prepareSharedHistory() async throws {
        events.append("prepareSharedHistory")
        if let prepareError {
            throw prepareError
        }
    }

    func terminateCodex() async -> Bool {
        events.append("terminate")
        return terminateResult
    }

    func forceTerminateCodex() async -> Bool {
        events.append("forceTerminate")
        return forceTerminateResult
    }

    func launchCodex() async throws {
        events.append("launch")
    }
}

private enum SharedHistoryTestError: Error, Equatable {
    case prepare
}

private func XCTAssertThrowsErrorAsync<T>(
    _ expression: @autoclosure () async throws -> T,
    _ errorHandler: (Error) -> Void = { _ in }
) async {
    do {
        _ = try await expression()
        XCTFail("Expected expression to throw")
    } catch {
        errorHandler(error)
    }
}
