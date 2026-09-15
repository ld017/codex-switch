import XCTest
@testable import CodexProviderCore
@testable import CodexProviderSwitcher

final class LiveSwitchPreflightTests: XCTestCase {
    func testLiveSub2APIPreflight() async throws {
        try XCTSkipUnless(
            ProcessInfo.processInfo.environment[
                "RUN_LIVE_SWITCH_PREFLIGHT"
            ] == "1"
        )

        let proxyPath = try XCTUnwrap(
            ProcessInfo.processInfo.environment[
                "LIVE_SHARED_HISTORY_PROXY"
            ]
        )
        let system = CodexSystem(
            sharedHistoryProxyURL: URL(fileURLWithPath: proxyPath)
        )
        try await system.validateProxyEnvironment()
        try await system.ensureSub2APIAvailable()
        try await system.validateTarget(.sub2api)
        try await system.prepareSharedHistoryLaunch()
    }
}
