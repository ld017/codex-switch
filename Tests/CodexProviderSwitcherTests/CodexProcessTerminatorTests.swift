import Darwin
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class CodexProcessTerminatorTests: XCTestCase {
    func testNativeForceTerminationSuccessDoesNotSendSignal() async {
        let controller = FakeCodexProcessController(
            runningPIDs: [123],
            nativeTerminationRemovesProcesses: true
        )
        let terminator = makeTerminator(controller)

        let result = await terminator.forceTerminateCodex()

        XCTAssertTrue(result)
        XCTAssertEqual(controller.signals, [])
    }

    func testFallbackKillsOnlyCapturedCodexPIDs() async {
        let controller = FakeCodexProcessController(
            runningPIDs: [123, 456],
            nativeTerminationRemovesProcesses: false
        )
        let terminator = makeTerminator(controller)

        let result = await terminator.forceTerminateCodex()

        XCTAssertTrue(result)
        XCTAssertEqual(
            controller.signals,
            [
                ProcessSignal(pid: 123, signal: SIGKILL),
                ProcessSignal(pid: 456, signal: SIGKILL),
            ]
        )
    }

    func testProcessAppearingAfterCaptureIsNeverKilled() async {
        let controller = FakeCodexProcessController(
            runningPIDs: [123],
            nativeTerminationRemovesProcesses: false,
            PIDToAddAfterNativeTermination: 999
        )
        let terminator = makeTerminator(controller)

        _ = await terminator.forceTerminateCodex()

        XCTAssertEqual(
            controller.signals,
            [ProcessSignal(pid: 123, signal: SIGKILL)]
        )
        XCTAssertTrue(controller.runningPIDs().contains(999))
    }

    func testReturnsFalseWhenCapturedPIDSurvivesFallbackSignal() async {
        let controller = FakeCodexProcessController(
            runningPIDs: [123],
            nativeTerminationRemovesProcesses: false,
            killSignalRemovesProcesses: false
        )
        let terminator = makeTerminator(controller)

        let result = await terminator.forceTerminateCodex()

        XCTAssertFalse(result)
    }

    private func makeTerminator(
        _ controller: FakeCodexProcessController
    ) -> CodexProcessTerminator {
        CodexProcessTerminator(
            controller: controller,
            nativeWaitAttempts: 1,
            fallbackWaitAttempts: 1,
            waitBetweenChecks: {}
        )
    }
}

private struct ProcessSignal: Equatable {
    let pid: pid_t
    let signal: Int32
}

@MainActor
private final class FakeCodexProcessController:
    CodexProcessControlling
{
    private var PIDs: Set<pid_t>
    private let nativeTerminationRemovesProcesses: Bool
    private let PIDToAddAfterNativeTermination: pid_t?
    private let killSignalRemovesProcesses: Bool
    private(set) var signals: [ProcessSignal] = []

    init(
        runningPIDs: Set<pid_t>,
        nativeTerminationRemovesProcesses: Bool,
        PIDToAddAfterNativeTermination: pid_t? = nil,
        killSignalRemovesProcesses: Bool = true
    ) {
        PIDs = runningPIDs
        self.nativeTerminationRemovesProcesses =
            nativeTerminationRemovesProcesses
        self.PIDToAddAfterNativeTermination =
            PIDToAddAfterNativeTermination
        self.killSignalRemovesProcesses = killSignalRemovesProcesses
    }

    func runningPIDs() -> Set<pid_t> {
        PIDs
    }

    func requestNativeForceTermination(for PIDs: Set<pid_t>) {
        if nativeTerminationRemovesProcesses {
            self.PIDs.subtract(PIDs)
        }
        if let PIDToAddAfterNativeTermination {
            self.PIDs.insert(PIDToAddAfterNativeTermination)
        }
    }

    func sendSignal(_ signal: Int32, to PID: pid_t) {
        signals.append(ProcessSignal(pid: PID, signal: signal))
        if killSignalRemovesProcesses {
            PIDs.remove(PID)
        }
    }
}
