import XCTest
@testable import CodexProviderCore
@testable import CodexProviderSwitcher

final class Sub2APIServiceControllerTests: XCTestCase {
    func testAlreadyOnlineRunsNoContainerCommands() async throws {
        let runner = FakeContainerCommandRunner()
        let stackRunner = FakeSub2APIStackRunner()
        let probe = FakeSub2APIHealthProbe(results: [true])
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe
        )

        try await controller.ensureAvailable()

        XCTAssertEqual(runner.arguments, [])
        XCTAssertEqual(stackRunner.runCount, 0)
    }

    func testStoppedSystemStartsBeforeListingContainers() async throws {
        let runner = FakeContainerCommandRunner(
            results: [
                .init(exitCode: 1, standardOutput: ""),
                .init(exitCode: 0, standardOutput: ""),
            ]
        )
        let stackRunner = FakeSub2APIStackRunner()
        let probe = FakeSub2APIHealthProbe(results: [false, true])
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe
        )

        try await controller.ensureAvailable()

        XCTAssertEqual(
            Array(runner.arguments.prefix(2)),
            [
                ["system", "status", "--format", "json"],
                [
                    "system", "start", "--disable-kernel-install",
                    "--timeout", "60",
                ],
            ]
        )
        XCTAssertEqual(stackRunner.runCount, 1)
    }

    func testRunsStackUpWhenServiceIsOffline() async throws {
        let runner = FakeContainerCommandRunner(
            results: [
                .init(exitCode: 0, standardOutput: "{}"),
            ]
        )
        let stackRunner = FakeSub2APIStackRunner()
        let probe = FakeSub2APIHealthProbe(results: [false, true])
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe
        )

        try await controller.ensureAvailable()

        XCTAssertEqual(stackRunner.runCount, 1)
    }

    func testStackUpFailureIsReported() async {
        let runner = FakeContainerCommandRunner(
            results: [
                .init(exitCode: 0, standardOutput: "{}"),
            ]
        )
        let stackRunner = FakeSub2APIStackRunner(
            result: .init(exitCode: 9, standardOutput: "")
        )
        let probe = FakeSub2APIHealthProbe(results: [false])
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe
        )

        await XCTAssertThrowsErrorAsync(
            try await controller.ensureAvailable()
        ) { error in
            XCTAssertEqual(
                error as? Sub2APIServiceControllerError,
                .stackStartFailed(exitCode: 9)
            )
        }
    }

    func testSystemStartFailureIsReported() async {
        let runner = FakeContainerCommandRunner(
            results: [
                .init(exitCode: 1, standardOutput: ""),
                .init(exitCode: 7, standardOutput: ""),
            ]
        )
        let stackRunner = FakeSub2APIStackRunner()
        let probe = FakeSub2APIHealthProbe(results: [false])
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe
        )

        await XCTAssertThrowsErrorAsync(
            try await controller.ensureAvailable()
        ) { error in
            XCTAssertEqual(
                error as? Sub2APIServiceControllerError,
                .systemStartFailed(exitCode: 7)
            )
        }
    }

    func testHealthDeadlineIsReported() async {
        let runner = FakeContainerCommandRunner(
            results: [
                .init(exitCode: 0, standardOutput: "{}"),
            ]
        )
        let stackRunner = FakeSub2APIStackRunner()
        let probe = FakeSub2APIHealthProbe(
            results: [false, false, false, false]
        )
        let controller = makeController(
            runner: runner,
            stackRunner: stackRunner,
            probe: probe,
            healthAttempts: 3
        )

        await XCTAssertThrowsErrorAsync(
            try await controller.ensureAvailable()
        ) { error in
            XCTAssertEqual(
                error as? Sub2APIServiceControllerError,
                .healthCheckTimedOut
            )
        }
    }

    private func makeController(
        runner: FakeContainerCommandRunner,
        stackRunner: FakeSub2APIStackRunner,
        probe: FakeSub2APIHealthProbe,
        healthAttempts: Int = 3
    ) -> Sub2APIServiceController {
        Sub2APIServiceController(
            runner: runner,
            stackRunner: stackRunner,
            healthProbe: probe,
            healthAttempts: healthAttempts,
            waitBetweenHealthChecks: {}
        )
    }
}

private final class FakeSub2APIStackRunner: Sub2APIStackRunning {
    private let result: ProcessResult
    private(set) var runCount = 0

    init(result: ProcessResult = .init(exitCode: 0, standardOutput: "")) {
        self.result = result
    }

    func runUp() async throws -> ProcessResult {
        runCount += 1
        return result
    }
}

private final class FakeContainerCommandRunner: ContainerCommandRunning {
    private var results: [ProcessResult]
    private(set) var arguments: [[String]] = []

    init(results: [ProcessResult] = []) {
        self.results = results
    }

    func run(arguments: [String]) async throws -> ProcessResult {
        self.arguments.append(arguments)
        return results.removeFirst()
    }
}

private final class FakeSub2APIHealthProbe: Sub2APIHealthProbing {
    private var results: [Bool]

    init(results: [Bool]) {
        self.results = results
    }

    func isOnline() async -> Bool {
        results.isEmpty ? false : results.removeFirst()
    }
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
