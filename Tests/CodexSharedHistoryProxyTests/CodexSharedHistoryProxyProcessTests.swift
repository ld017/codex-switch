import Darwin
import Foundation
import XCTest

final class CodexSharedHistoryProxyProcessTests: XCTestCase {
    private var temporaryDirectory: URL!
    private var fakeCLIURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true
        )
        fakeCLIURL = temporaryDirectory.appendingPathComponent("fake-cli")
        try Data(fakeCLIScript.utf8).write(to: fakeCLIURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o700)],
            ofItemAtPath: fakeCLIURL.path
        )
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporaryDirectory)
    }

    func testSignalPendingBeforeSpawnIsForwardedAfterChildLaunch() throws {
        let readyURL = temporaryDirectory.appendingPathComponent("ready")
        let supervisorReadyURL = temporaryDirectory
            .appendingPathComponent("supervisor-ready")
        let running = try launchProxy(
            mode: "pending-signal",
            extraEnvironment: [
                "CODEX_PROVIDER_PROXY_TEST_READY_FILE": readyURL.path,
                "CODEX_PROVIDER_PROXY_TEST_DELAY_USEC": "750000",
                "CODEX_PROVIDER_PROXY_TEST_SUPERVISOR_READY_FILE":
                    supervisorReadyURL.path,
            ]
        )
        defer { forceKillIfRunning(running) }

        guard waitForFile(readyURL, timeout: 2) else {
            return XCTFail("Proxy did not expose debug startup readiness")
        }

        XCTAssertEqual(Darwin.kill(running.process.processIdentifier, SIGTERM), 0)
        usleep(50_000)
        XCTAssertTrue(running.process.isRunning)
        XCTAssertTrue(waitForFile(supervisorReadyURL, timeout: 2))

        guard waitForExit(running, timeout: 3) else {
            return XCTFail("Proxy did not exit after pending signal delivery")
        }
        XCTAssertEqual(running.process.terminationReason, .uncaughtSignal)
        XCTAssertEqual(running.process.terminationStatus, SIGTERM)
    }

    func testSignalAfterChildExitTerminatesWrapperDuringOutputDrain() throws {
        let childExitedURL = temporaryDirectory
            .appendingPathComponent("child-exited")
        let holderPIDURL = temporaryDirectory.appendingPathComponent("holder-pid")
        let running = try launchProxy(
            mode: "hold-output",
            extraEnvironment: [
                "CODEX_TEST_CHILD_EXITED": childExitedURL.path,
                "CODEX_TEST_HOLDER_PID": holderPIDURL.path,
            ]
        )
        defer {
            forceKillIfRunning(running)
            killPID(in: holderPIDURL)
        }

        guard waitForFile(childExitedURL, timeout: 2) else {
            return XCTFail("Fake child did not exit")
        }

        XCTAssertEqual(Darwin.kill(running.process.processIdentifier, SIGTERM), 0)
        guard waitForExit(running, timeout: 1) else {
            return XCTFail("Proxy ignored signal during output drain")
        }
        XCTAssertEqual(running.process.terminationReason, .uncaughtSignal)
        XCTAssertEqual(running.process.terminationStatus, SIGTERM)
    }

    func testEarlyChildStdinClosePreservesChildExitStatus() throws {
        let childClosedURL = temporaryDirectory
            .appendingPathComponent("child-stdin-closed")
        let input = Pipe()
        let running = try launchProxy(
            mode: "early-close",
            extraEnvironment: [
                "CODEX_TEST_STDIN_CLOSED": childClosedURL.path,
            ],
            standardInput: input.fileHandleForReading
        )
        try input.fileHandleForReading.close()
        defer {
            try? input.fileHandleForWriting.close()
            forceKillIfRunning(running)
        }

        guard waitForFile(childClosedURL, timeout: 2) else {
            return XCTFail("Fake child did not close stdin")
        }
        try input.fileHandleForWriting.write(
            contentsOf: Data([0x61])
        )
        try input.fileHandleForWriting.close()

        guard waitForExit(running, timeout: 3) else {
            return XCTFail("Proxy did not preserve early child exit")
        }
        XCTAssertEqual(running.process.terminationReason, .exit)
        XCTAssertEqual(running.process.terminationStatus, 37)
    }

    func testOutputPumpFailureEscalatesWhenChildIgnoresSIGTERM() throws {
        let output = Pipe()
        try output.fileHandleForReading.close()
        let running = try launchProxy(
            mode: "ignore-term-output",
            standardOutput: output.fileHandleForWriting
        )
        try output.fileHandleForWriting.close()
        defer { forceKillIfRunning(running) }

        guard waitForExit(running, timeout: 3) else {
            return XCTFail("Proxy did not escalate pump failure")
        }
        XCTAssertEqual(running.process.terminationReason, .exit)
        XCTAssertNotEqual(running.process.terminationStatus, 0)
    }

    private func launchProxy(
        mode: String,
        extraEnvironment: [String: String] = [:],
        standardInput: Any? = FileHandle.nullDevice,
        standardOutput: Any? = FileHandle.nullDevice
    ) throws -> RunningProcess {
        let process = Process()
        let completion = DispatchSemaphore(value: 0)
        process.executableURL = proxyURL
        process.arguments = ["app-server", mode]
        process.standardInput = standardInput
        process.standardOutput = standardOutput
        process.standardError = FileHandle.nullDevice
        process.environment = ProcessInfo.processInfo.environment.merging(
            extraEnvironment
        ) { _, new in new }
        process.environment?["CODEX_PROVIDER_REAL_CLI"] = fakeCLIURL.path
        let childPIDURL = temporaryDirectory.appendingPathComponent(
            "child-\(UUID().uuidString).pid"
        )
        process.environment?["CODEX_TEST_CHILD_PID"] = childPIDURL.path
        process.terminationHandler = { _ in completion.signal() }
        try process.run()
        return RunningProcess(
            process: process,
            completion: completion,
            childPIDURL: childPIDURL
        )
    }

    private var proxyURL: URL {
        URL(fileURLWithPath: FileManager.default.currentDirectoryPath)
            .appendingPathComponent(".build/debug/CodexSharedHistoryProxy")
    }

    private func waitForFile(_ url: URL, timeout: TimeInterval) -> Bool {
        let deadline = Date().addingTimeInterval(timeout)
        while Date() < deadline {
            if FileManager.default.fileExists(atPath: url.path) {
                return true
            }
            usleep(10_000)
        }
        return false
    }

    private func waitForExit(
        _ running: RunningProcess,
        timeout: TimeInterval
    ) -> Bool {
        running.completion.wait(timeout: .now() + timeout) == .success
    }

    private func forceKillIfRunning(_ running: RunningProcess) {
        guard running.process.isRunning else {
            return
        }
        _ = Darwin.kill(running.process.processIdentifier, SIGKILL)
        _ = running.completion.wait(timeout: .now() + 2)
        killPID(in: running.childPIDURL)
    }

    private func killPID(in url: URL) {
        guard let data = try? Data(contentsOf: url),
              let text = String(data: data, encoding: .utf8),
              let pid = pid_t(text)
        else {
            return
        }
        _ = Darwin.kill(pid, SIGKILL)
    }

    private var fakeCLIScript: String {
        """
        #!/bin/sh
        printf '%s' "$$" > "${CODEX_TEST_CHILD_PID:?}"
        case "${2-}" in
            pending-signal)
                exec /bin/sleep 10
                ;;
            hold-output)
                parent=$$
                (
                    trap '' HUP INT TERM
                    while /bin/kill -0 "$parent" 2>/dev/null; do
                        /bin/sleep 0.01
                    done
                    : > "${CODEX_TEST_CHILD_EXITED:?}"
                    /bin/sleep 10
                ) &
                /usr/bin/printf '%s' "$!" > "${CODEX_TEST_HOLDER_PID:?}"
                exit 29
                ;;
            early-close)
                exec 0<&-
                : > "${CODEX_TEST_STDIN_CLOSED:?}"
                /bin/sleep 1
                exit 37
                ;;
            ignore-term-output)
                trap '' HUP INT TERM PIPE
                while true; do
                    /usr/bin/printf 'x' 2>/dev/null || :
                done
                ;;
            *)
                exit 99
                ;;
        esac
        """
    }
}

private struct RunningProcess {
    let process: Process
    let completion: DispatchSemaphore
    let childPIDURL: URL
}
