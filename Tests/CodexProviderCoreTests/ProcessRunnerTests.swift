import Foundation
import XCTest
@testable import CodexProviderCore

final class ProcessRunnerTests: XCTestCase {
    func testCapturesOutputLargerThanPipeBuffer() throws {
        let result = try ProcessRunner.run(
            executable: URL(fileURLWithPath: "/bin/sh"),
            arguments: [
                "-c",
                "i=0; while [ $i -lt 20000 ]; do echo 1234567890; i=$((i+1)); done",
            ],
            captureOutput: true,
            timeout: 10
        )

        XCTAssertEqual(result.exitCode, 0)
        XCTAssertGreaterThan(result.standardOutput.utf8.count, 200_000)
        XCTAssertTrue(result.standardOutput.hasSuffix("1234567890\n"))
    }

    func testTerminatesTimedOutProcess() {
        let start = Date()

        XCTAssertThrowsError(
            try ProcessRunner.run(
                executable: URL(fileURLWithPath: "/bin/sleep"),
                arguments: ["10"],
                captureOutput: true,
                timeout: 0.1
            )
        ) { error in
            XCTAssertEqual(error as? ProcessRunnerError, .timedOut)
        }
        XCTAssertLessThan(Date().timeIntervalSince(start), 3)
    }

    func testDoesNotWaitForDescendantThatInheritedOutputPipe() throws {
        let start = Date()

        let result = try ProcessRunner.run(
            executable: URL(fileURLWithPath: "/bin/sh"),
            arguments: [
                "-c",
                "(sleep 3) & echo parent-finished",
            ],
            captureOutput: true,
            timeout: 1
        )

        XCTAssertEqual(result.exitCode, 0)
        XCTAssertEqual(result.standardOutput, "parent-finished\n")
        XCTAssertLessThan(Date().timeIntervalSince(start), 1)
    }
}
