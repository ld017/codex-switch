import XCTest
@testable import CodexProviderSwitcher

final class SwitchDiagnosticsTests: XCTestCase {
    private var temporaryDirectory: URL!

    override func setUpWithError() throws {
        temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true
        )
    }

    override func tearDownWithError() throws {
        try FileManager.default.removeItem(at: temporaryDirectory)
    }

    func testStagedErrorPreservesStageAndUnderlyingDescription() {
        let error = StagedSwitchError(
            stage: .sub2APIRecovery,
            underlying: DiagnosticStubError(description: "端口不可用")
        )

        XCTAssertEqual(
            error.localizedDescription,
            "Sub2API 恢复：端口不可用"
        )
    }

    func testWrappingAnExistingStagedErrorDoesNotHideOriginalStage() {
        let original = StagedSwitchError(
            stage: .proxyCheck,
            underlying: DiagnosticStubError(description: "系统代理未开启")
        )

        let wrapped = StagedSwitchError.wrapping(
            original,
            stage: .targetValidation
        )

        XCTAssertEqual(wrapped.stage, .proxyCheck)
        XCTAssertEqual(
            wrapped.localizedDescription,
            "代理检查：系统代理未开启"
        )
    }

    func testLoggerRedactsBearerCredentialsAndURLUserInfo() throws {
        let logURL = temporaryDirectory.appendingPathComponent("switcher.log")
        let logger = SwitchDiagnosticLogger(
            logURL: logURL,
            maximumBytes: 200_000
        )

        logger.record(
            StagedSwitchError(
                stage: .proxyCheck,
                underlying: DiagnosticStubError(
                    description:
                        "Bearer abc123 http://private:secret@192.168.65.1:7892"
                )
            )
        )

        let output = try String(contentsOf: logURL, encoding: .utf8)
        XCTAssertTrue(output.contains("代理检查"))
        XCTAssertTrue(output.contains("192.168.65.1:7892"))
        XCTAssertFalse(output.contains("abc123"))
        XCTAssertFalse(output.contains("private"))
        XCTAssertFalse(output.contains("secret"))
    }

    func testLoggerKeepsNewestDataWithinConfiguredLimit() throws {
        let logURL = temporaryDirectory.appendingPathComponent("switcher.log")
        let logger = SwitchDiagnosticLogger(
            logURL: logURL,
            maximumBytes: 240
        )

        for index in 0..<20 {
            logger.record(
                StagedSwitchError(
                    stage: .configuration,
                    underlying: DiagnosticStubError(
                        description: "entry-\(index)-"
                            + String(repeating: "x", count: 24)
                    )
                )
            )
        }

        let data = try Data(contentsOf: logURL)
        let output = String(decoding: data, as: UTF8.self)
        XCTAssertLessThanOrEqual(data.count, 240)
        XCTAssertTrue(output.contains("entry-19"))
        XCTAssertFalse(output.contains("entry-0-"))
    }
}

private struct DiagnosticStubError: LocalizedError {
    let description: String

    var errorDescription: String? { description }
}
