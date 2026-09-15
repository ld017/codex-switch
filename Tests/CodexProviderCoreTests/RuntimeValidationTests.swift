import Foundation
import XCTest
@testable import CodexProviderCore

final class RuntimeValidationTests: XCTestCase {
    func testDoctorAcceptsSuccessfulConfigCheck() {
        let output = """
        {
          "overallStatus": "warning",
          "checks": {
            "config.load": {
              "status": "ok",
              "summary": "config loaded"
            }
          }
        }
        """

        XCTAssertTrue(
            DoctorResult(exitCode: 1, standardOutput: output)
                .isConfigurationValid
        )
    }

    func testDoctorRejectsMissingOrFailedConfigCheck() {
        XCTAssertFalse(
            DoctorResult(
                exitCode: 1,
                standardOutput: #"{"checks":{}}"#
            ).isConfigurationValid
        )
        XCTAssertFalse(
            DoctorResult(
                exitCode: 1,
                standardOutput: """
                {"checks":{"config.load":{"status":"error"}}}
                """
            ).isConfigurationValid
        )
    }

    func testReadsChatGPTAuthModeWithoutExposingTokens() throws {
        let data = Data(
            """
            {
              "auth_mode": "chatgpt",
              "tokens": {"access_token": "secret"}
            }
            """.utf8
        )

        XCTAssertEqual(try AuthInspector.authMode(from: data), "chatgpt")
    }

    func testRejectsAuthWithoutMode() {
        XCTAssertThrowsError(
            try AuthInspector.authMode(from: Data(#"{"tokens":{}}"#.utf8))
        )
    }
}
