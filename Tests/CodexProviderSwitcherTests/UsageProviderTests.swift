import XCTest
import CodexProfileCore
@testable import CodexProviderSwitcher

@MainActor
final class UsageProviderTests: XCTestCase {
    private func fixture() throws -> (URL, ProfileStore) {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        try FileManager.default.createDirectory(at: paths.switcherHome, withIntermediateDirectories: true)
        let config = AppConfig(profiles: [.init(id: "1", label: "One"), .init(id: "2", label: "Two")], activeProfile: "1")
        try JSONEncoder().encode(config).write(to: paths.configURL)
        try FileManager.default.createDirectory(at: paths.liveCodexHome, withIntermediateDirectories: true)
        try Data("{}".utf8).write(to: paths.liveAuthURL)
        let vault = FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL)
        for id in ["1", "2"] { try vault.saveAuthBlob(Data("{}".utf8), profileID: id) }
        return (home, ProfileStore(authVault: vault, environment: paths.environment))
    }

    private func snapshot(_ used: Int) -> UsageSnapshot {
        UsageSnapshot(planType: "plus", creditsRemaining: nil, primaryUsedPercent: used,
            primaryResetAt: nil, secondaryUsedPercent: 0, secondaryResetAt: nil, fetchedAt: Date())
    }

    func testQuotaPublishesBeforeTokenAndQueriesNeverOverlap() async throws {
        let (home, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let tracker = FetchTracker()
        let quota = self.snapshot(37)
        let provider = UsageProvider(store: store, fetch: { _, _, _, _, onQuota in
            await tracker.enter()
            await onQuota(quota)
            try await Task.sleep(for: .milliseconds(80))
            await tracker.leave()
            return quota
        })
        var sawPartial = false
        var sawStart = false
        var sawFinish = false
        provider.onChange = {
            sawStart = sawStart || provider.isRefreshing
            if provider.isRefreshing && store.statuses.values.contains(where: { $0.snapshot?.primaryUsedPercent == 37 }) {
                sawPartial = true
            }
            sawFinish = sawFinish || !provider.isRefreshing
        }
        provider.refreshAll(force: true)
        while provider.isRefreshing { try await Task.sleep(for: .milliseconds(10)) }
        XCTAssertTrue(sawStart)
        XCTAssertTrue(sawPartial, "Quota must publish while token fetch is still pending")
        XCTAssertTrue(sawFinish)
        let maximum = await tracker.maximum
        XCTAssertEqual(maximum, 1, "Refreshing should start at most one CLI at a time")
    }

    func testRepeatedForceRequestsCoalesceIntoOneFollowingCycle() async throws {
        let (home, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let tracker = FetchTracker()
        let value = self.snapshot(25)
        let provider = UsageProvider(store: store, fetch: { _, _, _, _, _ in
            await tracker.enter()
            try await Task.sleep(for: .milliseconds(30))
            await tracker.leave()
            return value
        })
        provider.refreshAll(force: true)
        provider.refreshAll(force: true)
        provider.refreshAll(force: true)
        while provider.isRefreshing { try await Task.sleep(for: .milliseconds(10)) }
        let count = await tracker.count
        XCTAssertEqual(count, 4, "Two profiles in initial cycle and one merged follow-up cycle")
    }

    func testCancellationDropsLateResultsAndPendingForce() async throws {
        let (home, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let tracker = FetchTracker()
        let value = self.snapshot(99)
        let provider = UsageProvider(store: store, fetch: { _, _, _, _, onQuota in
            await tracker.enter()
            try? await Task.sleep(for: .milliseconds(80))
            await onQuota(value)
            await tracker.leave()
            return value
        })
        provider.refreshAll(force: true)
        while await tracker.count == 0 { try await Task.sleep(for: .milliseconds(5)) }
        provider.refreshAll(force: true)
        provider.cancelRefreshes()
        try await Task.sleep(for: .milliseconds(150))
        XCTAssertFalse(provider.isRefreshing)
        XCTAssertFalse(store.statuses.values.contains(where: { $0.snapshot?.primaryUsedPercent == 99 }))
        let count = await tracker.count
        XCTAssertEqual(count, 1)
    }

    func testReplacementRefreshWaitsForCancelledFetchToDrain() async throws {
        let (home, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let tracker = FetchTracker()
        let value = self.snapshot(40)
        let provider = UsageProvider(store: store, fetch: { _, _, _, _, _ in
            await tracker.enter()
            // Models process shutdown that cannot finish at the instant of cancel.
            await Task.detached { try? await Task.sleep(for: .milliseconds(100)) }.value
            await tracker.leave()
            return value
        })
        provider.refreshAll(force: true)
        while await tracker.count == 0 { try await Task.sleep(for: .milliseconds(5)) }
        provider.cancelRefreshes()
        provider.refreshAll(force: true)
        while provider.isRefreshing { try await Task.sleep(for: .milliseconds(10)) }
        let maximum = await tracker.maximum
        XCTAssertEqual(maximum, 1, "Replacement cycle must await the previous CLI teardown")
    }

    // Break caught: auth is captured before a warmup batch wait and reused
    // after renewal has replaced the saved credential while it was queued.
    func testQueuedRefreshReloadsAuthAfterTheBatchDrains() async throws {
        let (home, store) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        let coordinator = CLIWorkCoordinator()
        let batch = try await coordinator.acquireBatch()
        let tracker = CapturedRefreshAuth()
        let value = self.snapshot(4)
        let provider = UsageProvider(store: store, coordinator: coordinator, fetch: { id, auth, _, _, _ in
            await tracker.record(id, auth)
            return value
        })
        provider.refreshAll(force: true)
        // The batch keeps all fetch closures blocked while groupContexts has
        // time to read the old blob. This is a real isolated file-vault edit.
        try await Task.sleep(for: .milliseconds(80))
        let replacement = Data("{\"credential_revision\":\"after-renewal\"}".utf8)
        let vault = FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL)
        try vault.saveAuthBlob(replacement, profileID: "2")
        let beforeRelease = await tracker.values
        XCTAssertTrue(beforeRelease.isEmpty)
        await coordinator.release(batch)
        while provider.isRefreshing { try await Task.sleep(for: .milliseconds(5)) }
        let fetched = await tracker.values
        XCTAssertEqual(fetched["2"], replacement)
    }

    func testTokenTTLAndPartialDataKeepFreshFields() throws {
        let now = Date(timeIntervalSince1970: 10_000)
        let days = try JSONDecoder().decode([TokenActivity.Day].self,
            from: Data("[{\"startDate\":\"2026-10-18\",\"tokens\":7}]".utf8))
        var cached = self.snapshot(1)
        cached.tokenActivity = TokenActivity(lifetimeTokens: 100, dailyUsageBuckets: days, fetchedAt: now)
        cached.tokenActivityUnavailable = false
        XCTAssertFalse(UsageProvider.needsTokenRefresh(cached: cached, force: false, now: now.addingTimeInterval(899)))
        XCTAssertTrue(UsageProvider.needsTokenRefresh(cached: cached, force: false, now: now.addingTimeInterval(901)))
        XCTAssertTrue(UsageProvider.needsTokenRefresh(cached: cached, force: true, now: now))
        var partial = self.snapshot(5)
        partial.tokenActivity = TokenActivity(lifetimeTokens: 120, dailyUsageBuckets: nil, fetchedAt: now.addingTimeInterval(60))
        let merged = UsageProvider.mergingTokenActivity(partial, cached: cached)
        XCTAssertEqual(merged.tokenActivity?.lifetimeTokens, 120)
        XCTAssertEqual(merged.tokenActivity?.dailyUsageBuckets, days)
        XCTAssertEqual(merged.tokenActivity?.fetchedAt, now)
        XCTAssertEqual(merged.tokenActivityUnavailable, true)
    }
}

private actor FetchTracker {
    var count = 0
    var maximum = 0
    private var running = 0
    func enter() { count += 1; running += 1; maximum = max(maximum, running) }
    func leave() { running -= 1 }
}

private actor CapturedRefreshAuth {
    private(set) var values: [String: Data] = [:]
    func record(_ id: String, _ data: Data) { values[id] = data }
}
