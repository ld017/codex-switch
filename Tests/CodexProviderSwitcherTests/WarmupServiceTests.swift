import XCTest
import CodexProfileCore
@testable import CodexProviderSwitcher

private actor WarmupTransportProbe {
    enum Mode { case confirm, existing, waitAfterIntent }
    let mode: Mode
    let store: WarmupStore
    var reads: [String: Int] = [:]
    var started: [String: Date] = [:]
    var sends = 0
    var active = 0
    var maximum = 0
    init(mode: Mode, store: WarmupStore) { self.mode = mode; self.store = store }
    func observation(identity: String, at: Date, reset: Date) -> WarmupQuotaObservation {
        .init(accountIdentity: identity, observedAt: at,
            windows: [.init(poolID: "codex", usedPercent: 0, windowDurationMinutes: 300, resetsAt: reset)])
    }
    func read(_ data: Data) throws -> WarmupQuotaObservation {
        let identity = try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: data))
        let count = reads[identity, default: 0]; reads[identity] = count + 1
        if let sent = started[identity] {
            return observation(identity: identity, at: sent.addingTimeInterval(count == 2 ? 15 : 65), reset: sent.addingTimeInterval(18000))
        }
        let at = Date().addingTimeInterval(count == 0 ? -10 : -5)
        return observation(identity: identity, at: at, reset: at.addingTimeInterval(18000))
    }
    func send(identity: String, before: WarmupTransport.BeforeSend) async throws -> WarmupSendResult {
        if mode == .existing { throw CLIWarmupError.existingWindow(resetAt: Date().addingTimeInterval(1200)) }
        try await before()
        // Exercise the real fsynced store boundary, rather than mocking it.
        XCTAssertTrue(try store.load().runs.contains { $0.key.accountIdentity == identity && $0.status == .sending })
        sends += 1; active += 1; maximum = max(maximum, active)
        defer { active -= 1 }
        let now = Date(); started[identity] = now
        try await Task.sleep(for: mode == .waitAfterIntent ? .seconds(60) : .milliseconds(20))
        return .init(completed: true, firstObservation: observation(identity: identity, at: now, reset: now.addingTimeInterval(18000)),
                     model: "fixture", sentAt: now, completedAt: Date())
    }
}

@MainActor
final class WarmupServiceTests: XCTestCase {
    private func fixture(_ mode: WarmupTransportProbe.Mode, count: Int = 1) -> (URL, WarmupViewModel, WarmupService, WarmupTransportProbe, WarmupStore) {
        let home = URL(fileURLWithPath: "/Users/Shared").appendingPathComponent("warmup-service-" + UUID().uuidString)
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        let store = WarmupStore(paths: paths), model = WarmupViewModel()
        let probe = WarmupTransportProbe(mode: mode, store: store)
        let profiles = (1...count).map { WarmupProfileItem(id: "id-\($0)", label: "Fixture \($0)", isAvailable: true) }
        let transport = WarmupTransport(observe: { data, _ in try await probe.read(data) },
            send: { _, identity, _, _, before in try await probe.send(identity: identity, before: before) })
        let service = WarmupService(viewModel: model, paths: paths, profiles: { profiles }, transport: transport,
            timing: WarmupTiming(baselineGap: 0, verificationOffsets: [0, 0], preparationBudget: 5, batchBudget: 5),
            auth: { id in
                let payload = try JSONSerialization.data(withJSONObject: ["email": "\(id)@example.invalid", "exp": Date().addingTimeInterval(3600).timeIntervalSince1970,
                    "https://api.openai.com/auth": ["chatgpt_account_id": id]]).base64EncodedString()
                return try JSONSerialization.data(withJSONObject: ["tokens": ["account_id": id, "access_token": "e30.\(payload).sig"]])
            })
        service.start(); model.draft.accounts = Set(profiles.map(\.id))
        return (home, model, service, probe, store)
    }

    func testFourAccountsConfirmOnlyAfterDurableIntentAndIndependentSamples() async throws {
        let (home, model, service, probe, store) = fixture(.confirm, count: 4)
        defer { service.stop(); try? FileManager.default.removeItem(at: home) }
        model.runNow(); await service.waitUntilIdle()
        let sends = await probe.sends, maximum = await probe.maximum
        XCTAssertEqual(sends, 4); XCTAssertEqual(maximum, 4)
        XCTAssertEqual(try store.load().runs.filter { $0.status == .confirmed }.count, 4)
        XCTAssertTrue(model.results.allSatisfy { $0.statusTitle == "服务器窗口已确认" })
        XCTAssertNil(try store.load().plan, "Immediate execution must not enable a schedule")
    }

    func testExistingWindowsNeverCommitSendIntent() async throws {
        let (home, model, service, probe, store) = fixture(.existing)
        defer { service.stop(); try? FileManager.default.removeItem(at: home) }
        model.runNow(); await service.waitUntilIdle()
        let sends = await probe.sends
        XCTAssertEqual(sends, 0)
        XCTAssertEqual(try store.load().runs.first?.status, .existingWindow)
        XCTAssertEqual(model.results.first?.statusTitle, "已有窗口")
    }

    func testCancellationAfterIntentRetiresAsUnknownWithoutResend() async throws {
        let (home, model, service, probe, store) = fixture(.waitAfterIntent)
        defer { service.stop(); try? FileManager.default.removeItem(at: home) }
        model.runNow()
        for _ in 0..<500 {
            if await probe.sends == 1 { break }
            try await Task.sleep(for: .milliseconds(2))
        }
        try await service.cancelForAccountMutation()
        XCTAssertEqual(try store.load().runs.first?.status, .unknown)
        model.runNow(); await service.waitUntilIdle()
        let sends = await probe.sends
        XCTAssertEqual(sends, 1)
        XCTAssertEqual(try store.load().runs.first?.status, .unknown)
    }
}
