import XCTest
@testable import CodexProviderSwitcher

final class ClashReadinessGuardTests: XCTestCase {
    func testReadyWhenPortSystemProxyAndTunAreActive() {
        let guardrail = ClashReadinessGuard(
            snapshot: FakeClashReadinessSnapshot(
                portOpen: true,
                systemProxy: readySystemProxy,
                interfaces: readyInterfaces
            )
        )

        XCTAssertEqual(guardrail.status(), .ready)
    }

    func testReportsClosedLocalPort() {
        let guardrail = ClashReadinessGuard(
            snapshot: FakeClashReadinessSnapshot(
                portOpen: false,
                systemProxy: readySystemProxy,
                interfaces: readyInterfaces
            )
        )

        XCTAssertEqual(guardrail.status(), .localPortUnavailable)
    }

    func testReportsDisabledSystemProxy() {
        let guardrail = ClashReadinessGuard(
            snapshot: FakeClashReadinessSnapshot(
                portOpen: true,
                systemProxy: "HTTPEnable : 0",
                interfaces: readyInterfaces
            )
        )

        XCTAssertEqual(guardrail.status(), .systemProxyDisabled)
    }

    func testReportsDisabledTun() {
        let guardrail = ClashReadinessGuard(
            snapshot: FakeClashReadinessSnapshot(
                portOpen: true,
                systemProxy: readySystemProxy,
                interfaces: "utun0: flags=8051<UP>"
            )
        )

        XCTAssertEqual(guardrail.status(), .tunDisabled)
    }
}

private struct FakeClashReadinessSnapshot: ClashReadinessSnapshotting {
    let portOpen: Bool
    let systemProxy: String
    let interfaces: String

    func localProxyPortIsOpen() -> Bool { portOpen }
    func systemProxyDescription() -> String { systemProxy }
    func interfaceDescription() -> String { interfaces }
}

private let readySystemProxy = """
HTTPEnable : 1
HTTPPort : 7897
HTTPProxy : 127.0.0.1
HTTPSEnable : 1
HTTPSPort : 7897
HTTPSProxy : 127.0.0.1
"""

private let readyInterfaces = """
utun1024: flags=8051<UP,POINTOPOINT,RUNNING,MULTICAST>
    inet 198.18.0.1 --> 198.18.0.1 netmask 0xfffffffc
"""
