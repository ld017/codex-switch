import XCTest
@testable import CodexProviderCore
@testable import CodexProviderSwitcher

@MainActor
final class SwitchPreparationCoordinatorTests: XCTestCase {
    func testSub2APIPreparationRunsGuardServiceValidationAndHistoryInOrder() async throws {
        let system = FakeSwitchPreparationSystem()
        let coordinator = SwitchPreparationCoordinator(system: system)

        try await coordinator.prepare(target: .sub2api)

        XCTAssertEqual(
            system.events,
            ["proxyGuard", "ensureSub2API", "validate", "sharedHistory"]
        )
    }

    func testOpenAIPreparationSkipsSub2APIService() async throws {
        let system = FakeSwitchPreparationSystem()
        let coordinator = SwitchPreparationCoordinator(system: system)

        try await coordinator.prepare(target: .openai)

        XCTAssertEqual(
            system.events,
            ["proxyGuard", "validate", "sharedHistory"]
        )
    }

    func testProxyConflictStopsBeforeServiceAndValidation() async {
        let system = FakeSwitchPreparationSystem()
        system.proxyError = TestPreparationError.proxy
        let coordinator = SwitchPreparationCoordinator(system: system)

        await XCTAssertThrowsPreparationError(
            try await coordinator.prepare(target: .sub2api)
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .proxyCheck
            )
        }
        XCTAssertEqual(system.events, ["proxyGuard"])
    }

    func testServiceFailureStopsBeforeTargetValidation() async {
        let system = FakeSwitchPreparationSystem()
        system.serviceError = TestPreparationError.service
        let coordinator = SwitchPreparationCoordinator(system: system)

        await XCTAssertThrowsPreparationError(
            try await coordinator.prepare(target: .sub2api)
        ) { error in
            XCTAssertEqual(
                (error as? StagedSwitchError)?.stage,
                .sub2APIRecovery
            )
        }
        XCTAssertEqual(system.events, ["proxyGuard", "ensureSub2API"])
    }
}

@MainActor
private final class FakeSwitchPreparationSystem: SwitchPreparationSystem {
    var events: [String] = []
    var proxyError: Error?
    var serviceError: Error?

    func validateProxyEnvironment() async throws {
        events.append("proxyGuard")
        if let proxyError {
            throw proxyError
        }
    }

    func ensureSub2APIAvailable() async throws {
        events.append("ensureSub2API")
        if let serviceError {
            throw serviceError
        }
    }

    func validateTarget(_ target: Provider) async throws {
        events.append("validate")
    }

    func prepareSharedHistoryLaunch() async throws {
        events.append("sharedHistory")
    }
}

private enum TestPreparationError: Error, Equatable {
    case proxy
    case service
}

private func XCTAssertThrowsPreparationError<T>(
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
