import XCTest
import CodexProviderCore
@testable import CodexProviderSwitcher

@MainActor
private final class DiagnosticLatch {
    var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async { await withCheckedContinuation { waiters.append($0) } }
    func open() { let pending = waiters; waiters.removeAll(); pending.forEach { $0.resume() } }
}

@MainActor
private final class DiagnosticSource: ConnectionStatusSource {
    var provider: Provider = .openai
    var unreadable = false
    var online = false
    var portCalls = 0, environmentCalls = 0, readinessCalls = 0, sharedCalls = 0
    var active = 0, maximum = 0
    var portGate: DiagnosticLatch?
    var sharedGate: DiagnosticLatch?
    func currentProvider() throws -> Provider {
        if unreadable { throw CodexSystemError.configMissing }
        return provider
    }
    func sub2APIIsOnline() async -> Bool {
        portCalls += 1; active += 1; maximum = max(maximum, active)
        let result = online
        if let gate = portGate { await gate.wait() } // Deliberately ignores cancellation.
        active -= 1
        return result
    }
    func proxyEnvironmentConflicts() async -> [ProxyEnvironmentConflict] {
        environmentCalls += 1
        return [.init(variable: "HTTP_PROXY")]
    }
    func clashReadinessStatus() async -> ClashReadinessStatus { readinessCalls += 1; return .systemProxyDisabled }
    func sharedHistoryIsConfigured() async -> Bool {
        sharedCalls += 1
        if let gate = sharedGate { await gate.wait() }
        return true
    }
}

@MainActor
final class ConnectionStatusControllerTests: XCTestCase {
    private func eventually(_ predicate: () -> Bool, file: StaticString = #filePath, line: UInt = #line) async {
        for _ in 0..<500 {
            if predicate() { return }
            try? await Task.sleep(for: .milliseconds(1))
        }
        XCTFail("Timed out waiting for diagnostic state", file: file, line: line)
    }
    private func settle() async { for _ in 0..<30 { await Task.yield() } }

    func testOpenAIProviderRefreshDoesNotRunDiagnostics() async {
        let source = DiagnosticSource()
        let controller = ConnectionStatusController(source: source)
        for _ in 0..<5 { controller.refreshProvider(); controller.requestActiveChannelStatus() }
        await settle()
        XCTAssertEqual(controller.snapshot.provider, .configured(.openai))
        XCTAssertEqual(source.portCalls + source.environmentCalls + source.readinessCalls + source.sharedCalls, 0)
        source.unreadable = true; controller.refreshProvider()
        XCTAssertEqual(controller.snapshot.provider, .unreadable)
        controller.stop()
    }
    func testSub2APIPortOnlyCacheExpiresAt60AndOnClockRollback() async {
        let source = DiagnosticSource(); source.provider = .sub2api
        var now = Date(timeIntervalSince1970: 1_000)
        let c = ConnectionStatusController(source: source, now: { now })
        c.refreshProvider(); c.requestActiveChannelStatus()
        await eventually { c.snapshot.sub2APICheckedAt != nil }
        XCTAssertEqual(source.environmentCalls + source.readinessCalls + source.sharedCalls, 0)
        now = now.addingTimeInterval(59); c.requestActiveChannelStatus(); await settle()
        XCTAssertEqual(source.portCalls, 1)
        now = now.addingTimeInterval(1); c.requestActiveChannelStatus()
        await eventually { source.portCalls == 2 && !c.snapshot.isRefreshingActiveChannel }
        now = now.addingTimeInterval(-1); c.requestActiveChannelStatus()
        await eventually { source.portCalls == 3 && !c.snapshot.isRefreshingActiveChannel }
        c.stop()
    }
    func testFullDiagnosticsAreCachedAndRepeatedOpensCoalesce() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch(); source.portGate = gate
        let c = ConnectionStatusController(source: source)
        c.refreshProvider()
        for _ in 0..<20 { c.requestDiagnostics() }
        await eventually { source.portCalls == 1 }
        source.portGate = nil; gate.open()
        await eventually { c.snapshot.diagnosticsCheckedAt != nil }
        for _ in 0..<20 { c.requestDiagnostics() }
        await settle()
        XCTAssertEqual(source.portCalls, 1)
        XCTAssertEqual(source.environmentCalls, 1); XCTAssertEqual(source.readinessCalls, 1); XCTAssertEqual(source.sharedCalls, 1)
        XCTAssertEqual(c.snapshot.proxyConflictVariables, ["HTTP_PROXY"])
        XCTAssertEqual(c.snapshot.clashReadiness, .systemProxyDisabled)
        XCTAssertEqual(c.snapshot.sharedHistoryConfigured, true)
        c.stop()
    }
    func testPortSamplingPromotesToAtMostOneFollowupFullBatch() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch()
        source.provider = .sub2api; source.portGate = gate
        let c = ConnectionStatusController(source: source)
        c.refreshProvider(); c.requestActiveChannelStatus()
        await eventually { source.portCalls == 1 }
        for _ in 0..<20 { c.requestDiagnostics(); c.requestActiveChannelStatus() }
        source.portGate = nil; gate.open()
        await eventually { c.snapshot.diagnosticsCheckedAt != nil }
        XCTAssertEqual(source.portCalls, 2); XCTAssertEqual(source.environmentCalls, 1)
        XCTAssertEqual(source.maximum, 1)
        c.stop()
    }
    func testProviderChangeWaitsForCancelledSourceAndDiscardsOldFailure() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch()
        source.provider = .sub2api; source.portGate = gate
        let c = ConnectionStatusController(source: source)
        c.refreshProvider(); c.requestActiveChannelStatus()
        await eventually { source.portCalls == 1 }
        source.provider = .openai; c.refreshProvider()
        source.online = true
        for _ in 0..<20 { c.requestDiagnostics() }
        await settle()
        XCTAssertEqual(source.portCalls, 1, "Cancelled outer task must still own its actual source slot")
        source.portGate = nil; gate.open()
        await eventually { c.snapshot.diagnosticsCheckedAt != nil }
        XCTAssertEqual(source.portCalls, 2); XCTAssertEqual(source.maximum, 1)
        XCTAssertEqual(c.snapshot.provider, .configured(.openai))
        XCTAssertEqual(c.snapshot.sub2APIOnline, true)
        c.stop()
    }
    func testInvalidationAfterRecoveryDropsFailureAndQueuesOnlyOneNewBatch() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch()
        source.provider = .sub2api; source.portGate = gate
        let c = ConnectionStatusController(source: source)
        c.refreshProvider(); c.requestDiagnostics()
        await eventually { source.portCalls == 1 }
        c.invalidate(); source.online = true
        for _ in 0..<20 { c.requestActiveChannelStatus(); c.requestDiagnostics() }
        await settle(); XCTAssertEqual(source.portCalls, 1)
        source.portGate = nil; gate.open()
        await eventually { c.snapshot.diagnosticsCheckedAt != nil }
        XCTAssertEqual(source.portCalls, 2); XCTAssertEqual(source.maximum, 1)
        XCTAssertEqual(c.snapshot.sub2APIOnline, true)
        c.stop()
    }
    func testStopDropsQueuedWorkAndLateResultWithoutEmittingAgain() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch()
        source.provider = .sub2api; source.portGate = gate
        let c = ConnectionStatusController(source: source)
        var emissions = 0; c.onChange = { _ in emissions += 1 }
        c.refreshProvider(); c.requestActiveChannelStatus()
        await eventually { source.portCalls == 1 }
        c.requestDiagnostics(); c.stop()
        let afterStop = emissions
        source.portGate = nil; gate.open()
        await eventually { source.active == 0 }
        await settle(); c.requestDiagnostics(); c.requestActiveChannelStatus(); c.refreshProvider()
        XCTAssertEqual(source.portCalls, 1); XCTAssertEqual(source.environmentCalls, 0)
        XCTAssertNil(c.snapshot.sub2APIOnline); XCTAssertNil(c.snapshot.diagnosticsCheckedAt)
        XCTAssertEqual(emissions, afterStop)
    }
    func testPortTimestampIsCompletionTimeEvenWhenRestOfBatchIsSlow() async {
        let source = DiagnosticSource(), gate = DiagnosticLatch(); source.sharedGate = gate
        var now = Date(timeIntervalSince1970: 1_000)
        let c = ConnectionStatusController(source: source, now: { now })
        c.refreshProvider(); c.requestDiagnostics()
        await eventually { source.sharedCalls == 1 }
        now = now.addingTimeInterval(70); gate.open()
        await eventually { c.snapshot.diagnosticsCheckedAt != nil }
        XCTAssertEqual(c.snapshot.sub2APICheckedAt, Date(timeIntervalSince1970: 1_000))
        XCTAssertEqual(c.snapshot.diagnosticsCheckedAt, now)
        XCTAssertEqual(ConnectionStatusPresentation.make(snapshot: c.snapshot, now: now).details[1].title,
            "本机 Sub2API 端口：检查未通过（缓存已过期）")
        source.provider = .sub2api
        // A provider refresh invalidates all previous-channel samples.
        c.refreshProvider(); XCTAssertNil(c.snapshot.sub2APICheckedAt)
        c.stop()
    }
}
