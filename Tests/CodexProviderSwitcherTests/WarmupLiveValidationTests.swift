import XCTest
import CodexProfileCore
@testable import CodexProviderSwitcher

/// Opt-in local acceptance check. Normal test/build/install runs never send.
@MainActor
final class WarmupLiveValidationTests: XCTestCase {
    func testAuthorizedLocalAccounts() async throws {
        guard ProcessInfo.processInfo.environment["RUN_AUTHORIZED_WARMUP_LIVE"] == "1" else {
            throw XCTSkip("Real account requests require an explicit opt-in")
        }
        let profiles = ProfileStore()
        let manager = AccountManager(currentProvider: { .openai }, beginOperation: { true }, finishOperation: {},
            prepareSharedHistory: {}, showError: { _, _ in }, onChange: {}, onIconChange: { _ in }, openWarmup: {}, store: profiles)
        let model = WarmupViewModel()
        let service = WarmupService(viewModel: model, profiles: { manager.warmupProfiles() }, auth: { id in
            try await manager.warmupAuthData(profileID: id)
        })
        service.start()
        defer { service.stop() }
        XCTAssertNil(model.controlLoadError)
        guard model.controlLoadError == nil else { return }
        guard model.savedPlan?.enabled != true else { throw XCTSkip("An enabled user plan is already scheduled") }
        let unsentOnly = ProcessInfo.processInfo.environment["WARMUP_VALIDATION_UNSENT_ONLY"] == "1"
        let recorded = Set(try WarmupStore(paths: AppPaths()).load().runs.map { $0.key.profileID })
        let ids = Array(model.profiles.filter { $0.isAvailable && (!unsentOnly || !recorded.contains($0.id)) }.prefix(4).map(\.id))
        XCTAssertEqual(ids.count, unsentOnly ? 1 : 4)
        model.draft.accounts = Set(ids)
        let started = Date()
        let sampler = Task {
            while !Task.isCancelled {
                _ = WarmupResources.snapshot()
                try? await Task.sleep(for: .milliseconds(100))
            }
        }
        model.runNow(); await service.waitUntilIdle()
        sampler.cancel(); await sampler.value
        let resources = WarmupResources.snapshot()
        let rows = model.results.map { row in
            ["status": row.statusTitle, "resetAt": row.resetAt.map { ISO8601DateFormatter().string(from: $0) } ?? "",
             "detail": row.detail]
        }
        let report: [String: Any] = ["elapsedSeconds": Date().timeIntervalSince(started), "rows": rows,
            "peakCLIs": resources.peakCLIs, "sampledPeakCLIBytes": resources.peakCLIBytes,
            "remainingCLIs": resources.activeCLIs, "message": model.lastMessage]
        let directory = URL(fileURLWithPath: FileManager.default.currentDirectoryPath).appendingPathComponent("artifacts/warmup-acceptance")
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try AtomicFileWriter.write(JSONSerialization.data(withJSONObject: report, options: [.prettyPrinted, .sortedKeys]),
            to: directory.appendingPathComponent(unsentOnly ? "live-unsent-result.json" : "live-result.json"))
        print("Live warmup acceptance: \(rows.map { $0["status"] ?? "" }.joined(separator: ", ")); remaining CLIs=\(resources.activeCLIs)")
        XCTAssertEqual(resources.activeCLIs, 0)
        XCTAssertEqual(model.results.count, ids.count)
        XCTAssertTrue(model.results.contains { $0.statusTitle == "服务器窗口已确认" || $0.statusTitle == "已有窗口" }, model.lastMessage)
    }
}
