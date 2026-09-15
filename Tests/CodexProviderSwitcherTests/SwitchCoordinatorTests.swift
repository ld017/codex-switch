import AppKit
import XCTest
@testable import CodexProviderCore
@testable import CodexProviderSwitcher

@MainActor
final class SwitchCoordinatorTests: XCTestCase {
    func testSuccessfulSwitchStopsCodexBeforePreparingLatestConfiguration() async throws {
        let system = FakeSwitchSystem()
        let coordinator = SwitchCoordinator(system: system)

        let result = try await coordinator.execute(
            target: .openai,
            confirmForceQuit: { false }
        )

        XCTAssertEqual(result, .switched)
        XCTAssertEqual(system.events, ["terminate", "prepare", "launch"])
    }

    func testCancellingForceQuitDoesNotTouchConfiguration() async throws {
        let system = FakeSwitchSystem()
        system.terminateResult = false
        let coordinator = SwitchCoordinator(system: system)

        let result = try await coordinator.execute(
            target: .openai,
            confirmForceQuit: { false }
        )

        XCTAssertEqual(result, .cancelled)
        XCTAssertEqual(system.events, ["terminate"])
    }

    func testApprovedForceQuitHappensBeforeConfigurationPreparation() async throws {
        let system = FakeSwitchSystem()
        system.terminateResult = false
        let coordinator = SwitchCoordinator(system: system)

        let result = try await coordinator.execute(
            target: .openai,
            confirmForceQuit: { true }
        )

        XCTAssertEqual(result, .switched)
        XCTAssertEqual(
            system.events,
            ["terminate", "forceTerminate", "prepare", "launch"]
        )
    }

    func testFailedConfirmedTerminationReportsTerminationStage() async {
        let system = FakeSwitchSystem()
        system.terminateResult = false
        system.forceTerminateResult = false
        let coordinator = SwitchCoordinator(system: system)

        await XCTAssertThrowsErrorAsync(
            try await coordinator.execute(
                target: .sub2api,
                confirmForceQuit: { true }
            )
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .codexTermination
            )
        }
        XCTAssertEqual(system.events, ["terminate", "forceTerminate"])
    }

    func testPreparationFailureReopensCodexWithoutRestoringAgain() async {
        let system = FakeSwitchSystem()
        system.prepareError = TestError.prepare
        let coordinator = SwitchCoordinator(system: system)

        await XCTAssertThrowsErrorAsync(
            try await coordinator.execute(
                target: .openai,
                confirmForceQuit: { false }
            )
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .configuration
            )
        }
        XCTAssertEqual(system.events, ["terminate", "prepare", "launch"])
    }

    func testLaunchFailureRestoresAndReopensPreviousConfiguration() async {
        let system = FakeSwitchSystem()
        system.launchErrors = [TestError.launch, nil]
        let coordinator = SwitchCoordinator(system: system)

        await XCTAssertThrowsErrorAsync(
            try await coordinator.execute(
                target: .openai,
                confirmForceQuit: { false }
            )
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .codexLaunch
            )
        }
        XCTAssertEqual(
            system.events,
            ["terminate", "prepare", "launch", "restore", "launch"]
        )
    }

    func testRestoreFailureSurfacesRecoveryErrorAndDoesNotClaimRecovery() async {
        let system = FakeSwitchSystem()
        system.launchErrors = [TestError.launch]
        system.restoreError = TestError.restore
        let coordinator = SwitchCoordinator(system: system)

        await XCTAssertThrowsErrorAsync(
            try await coordinator.execute(
                target: .openai,
                confirmForceQuit: { false }
            )
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .recovery
            )
        }
        XCTAssertEqual(
            system.events,
            ["terminate", "prepare", "launch", "restore"]
        )
    }

    func testApplicationTerminationIsBlockedOnlyDuringSwitch() {
        let delegate = AppDelegate()
        XCTAssertEqual(
            delegate.applicationShouldTerminate(NSApplication.shared),
            .terminateNow
        )

        XCTAssertTrue(delegate.switchActivity.begin())
        XCTAssertEqual(
            delegate.applicationShouldTerminate(NSApplication.shared),
            .terminateCancel
        )

        delegate.switchActivity.finish()
        XCTAssertEqual(
            delegate.applicationShouldTerminate(NSApplication.shared),
            .terminateNow
        )
    }
}

@MainActor
private final class FakeSwitchSystem: SwitchSystem {
    var events: [String] = []
    var terminateResult = true
    var forceTerminateResult = true
    var prepareError: Error?
    var restoreError: Error?
    var launchErrors: [Error?] = []

    func terminateCodex() async -> Bool {
        events.append("terminate")
        return terminateResult
    }

    func forceTerminateCodex() async -> Bool {
        events.append("forceTerminate")
        return forceTerminateResult
    }

    func prepareConfigurationSwitch(to target: Provider) async throws -> SwitchContext {
        events.append("prepare")
        if let prepareError {
            throw prepareError
        }
        return SwitchContext(
            previousProvider: .sub2api,
            backupURL: URL(fileURLWithPath: "/tmp/config-backup.toml")
        )
    }

    func restore(_ context: SwitchContext) async throws {
        events.append("restore")
        if let restoreError {
            throw restoreError
        }
    }

    func launchCodex() async throws {
        events.append("launch")
        if !launchErrors.isEmpty, let error = launchErrors.removeFirst() {
            throw error
        } else if !launchErrors.isEmpty {
            _ = launchErrors.removeFirst()
        }
    }
}

private enum TestError: Error, Equatable {
    case prepare
    case launch
    case restore
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
