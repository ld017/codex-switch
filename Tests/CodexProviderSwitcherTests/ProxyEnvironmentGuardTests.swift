import XCTest
@testable import CodexProviderSwitcher

final class ProxyEnvironmentGuardTests: XCTestCase {
    func testEmptyEnvironmentIsSafe() {
        let guardrail = ProxyEnvironmentGuard(
            reader: FakeProxyEnvironmentReader(values: [:])
        )

        XCTAssertEqual(guardrail.conflicts(), [])
    }

    func testLoopbackProxyEnvironmentIsSafe() {
        let guardrail = ProxyEnvironmentGuard(
            reader: FakeProxyEnvironmentReader(values: [
                "HTTP_PROXY": "http://127.0.0.1:7897",
                "HTTPS_PROXY": "http://localhost:7897",
                "ALL_PROXY": "socks5://[::1]:7897",
            ])
        )

        XCTAssertEqual(guardrail.conflicts(), [])
    }

    func testBridgeProxyIsDetectedForUppercaseAndLowercaseVariables() {
        let guardrail = ProxyEnvironmentGuard(
            reader: FakeProxyEnvironmentReader(values: [
                "HTTP_PROXY": "http://192.168.65.1:7897",
                "https_proxy": "http://user:secret@192.168.65.1:7897",
            ])
        )

        XCTAssertEqual(
            guardrail.conflicts(),
            [
                ProxyEnvironmentConflict(variable: "HTTP_PROXY"),
                ProxyEnvironmentConflict(variable: "https_proxy"),
            ]
        )
    }

    func testUnrelatedRemoteProxyIsNotClassifiedAsKnownSelfLoop() {
        let guardrail = ProxyEnvironmentGuard(
            reader: FakeProxyEnvironmentReader(values: [
                "HTTPS_PROXY": "https://proxy.example.test:8443",
            ])
        )

        XCTAssertEqual(guardrail.conflicts(), [])
    }

    func testConflictDescriptionDoesNotExposeProxyCredentials() {
        let guardrail = ProxyEnvironmentGuard(
            reader: FakeProxyEnvironmentReader(values: [
                "ALL_PROXY": "http://private-user:private-password@192.168.65.1:7897",
            ])
        )

        let description = guardrail.conflicts()[0].errorDescription ?? ""
        XCTAssertTrue(description.contains("ALL_PROXY"))
        XCTAssertFalse(description.contains("private-user"))
        XCTAssertFalse(description.contains("private-password"))
    }
}

private struct FakeProxyEnvironmentReader: ProxyEnvironmentReading {
    let values: [String: String]

    func value(for variable: String) -> String? {
        values[variable]
    }
}
