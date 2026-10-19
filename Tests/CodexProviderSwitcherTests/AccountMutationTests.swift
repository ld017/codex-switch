import XCTest
import AppKit
import CodexProfileCore
@testable import CodexProviderSwitcher

private actor AccountMutationLatch {
    private var opened = false
    private var waiters: [CheckedContinuation<Void, Never>] = []
    func wait() async {
        if opened { return }
        await withCheckedContinuation { waiters.append($0) }
    }
    func open() {
        opened = true
        let pending = waiters; waiters.removeAll()
        for continuation in pending { continuation.resume() }
    }
}

@MainActor
final class AccountMutationTests: XCTestCase {
    private func fixture(openWarmup: @escaping () -> Void = {}) throws -> (URL, ProfileStore, AccountManager) {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        let vault = FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL)
        let store = ProfileStore(authVault: vault, environment: paths.environment)
        let manager = AccountManager(currentProvider: { .openai }, beginOperation: { true },
            finishOperation: {}, prepareSharedHistory: {}, showError: { _, _ in },
            onChange: {}, onIconChange: { _ in }, openWarmup: openWarmup, store: store)
        return (home, store, manager)
    }

    func testWarmupHasOneMenuEntryAndOpensThroughTheProductionCallback() async throws {
        let opened = expectation(description: "warmup window callback")
        let (home, _, manager) = try fixture(openWarmup: { opened.fulfill() })
        defer { try? FileManager.default.removeItem(at: home) }
        let menu = NSMenu()
        manager.appendItems(to: menu)
        let entries = menu.items.filter { $0.title.hasPrefix("定时预热") }
        XCTAssertEqual(entries.count, 1)
        let item = try XCTUnwrap(entries.first)
        _ = manager.perform(try XCTUnwrap(item.action))
        await fulfillment(of: [opened], timeout: 1)
    }

    // Break caught: login/import/removal starts before warmup child cleanup,
    // or drops the mutation gate while waiting for that cleanup.
    func testMutationWaitsForWarmupCleanupWithGateClosed() async throws {
        let (home, store, manager) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let latch = AccountMutationLatch()
        var hookEntered = false
        var bodyEntered = false
        manager.beforeAccountMutation = {
            hookEntered = true
            await latch.wait()
        }
        let task = Task { @MainActor in
            try await manager.performAccountMutation { bodyEntered = true }
        }
        try await Task.sleep(for: .milliseconds(30))
        XCTAssertTrue(hookEntered)
        XCTAssertTrue(manager.hasAuthMutationInFlight)
        XCTAssertTrue(store.isAuthMutationInProgress())
        XCTAssertFalse(bodyEntered)
        await latch.open()
        try await task.value
        XCTAssertTrue(bodyEntered)
        XCTAssertFalse(manager.hasAuthMutationInFlight)
    }

    // Break caught: an unreaped-child rejection still allows an auth mutation,
    // or leaves the global mutation gate permanently set after rejection.
    func testCleanupRejectionPreventsMutationAndRestoresGate() async throws {
        let (home, _, manager) = try fixture()
        defer { try? FileManager.default.removeItem(at: home) }
        var bodyEntered = false
        manager.beforeAccountMutation = {
            throw CodexRPCError.requestFailed("child still running")
        }
        do {
            try await manager.performAccountMutation { bodyEntered = true }
            XCTFail("cleanup rejection must abort mutation")
        } catch {}
        XCTAssertFalse(bodyEntered)
        XCTAssertFalse(manager.hasAuthMutationInFlight)
    }
}
