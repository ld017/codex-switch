import AppKit
import CodexProfileCore
import SwiftUI
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class QuotaRowLayoutTests: XCTestCase {
    private func instant(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value)!
    }

    func testPrimaryResetUsesServerFieldAndNotSecondaryOrResetCardExpiry() {
        let now = instant("2026-10-20T08:10:00Z")
        let snapshot = UsageSnapshot(
            planType: "plus", creditsRemaining: nil,
            primaryUsedPercent: 20, primaryResetAt: instant("2026-10-20T12:31:00Z"),
            secondaryUsedPercent: 40, secondaryResetAt: instant("2026-10-20T13:15:00Z"),
            fetchedAt: now, primaryWindowDurationMins: 300,
            secondaryWindowDurationMins: 10_080,
            resetCreditsAvailable: 1,
            nextResetCreditExpiresAt: instant("2026-10-20T14:45:00Z"))

        let value = ProfileCardView.primaryResetPresentation(status: .available(snapshot), now: now)
        XCTAssertEqual(value?.text, "20:31 重置 · 余4时21分")
        XCTAssertFalse(value?.text.contains("21:15") == true)
        XCTAssertFalse(value?.text.contains("22:45") == true)
    }

    func testResetPresentationKeepsUsageRowHeightUnchanged() {
        let now = instant("2026-10-20T08:10:00Z")
        let value = QuotaResetPresentation.make(
            resetAt: instant("2026-10-20T12:31:00Z"), now: now, isCached: false)
        let withReset = UsageRow(label: "5小时", remainingPercent: 80,
            resetAt: instant("2026-10-20T12:31:00Z"), resetPresentation: value,
            isHighlighted: true, now: now)
        let countdownOnly = UsageRow(label: "周", remainingPercent: 60,
            resetAt: instant("2026-10-21T12:31:00Z"),
            isHighlighted: true, now: now)
        let resetHost = NSHostingView(rootView: withReset)
        let countdownHost = NSHostingView(rootView: countdownOnly)
        resetHost.frame.size = NSSize(width: 340, height: 20)
        countdownHost.frame.size = NSSize(width: 340, height: 20)
        resetHost.layoutSubtreeIfNeeded()
        countdownHost.layoutSubtreeIfNeeded()

        XCTAssertEqual(resetHost.fittingSize.height, countdownHost.fittingSize.height, accuracy: 1)
    }

    func testPrimaryResetUsesServerFieldAndKeepsTrackingObjectsAcrossMidnight() throws {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: home) }
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        try FileManager.default.createDirectory(at: paths.switcherHome, withIntermediateDirectories: true)
        try JSONEncoder().encode(AppConfig(profiles: [.init(id: "fixture", label: "Synthetic")], activeProfile: "fixture"))
            .write(to: paths.configURL)
        let store = ProfileStore(authVault: FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL), environment: paths.environment)
        var rebuilds = 0
        let accounts = AccountManager(currentProvider: { XCTFail("No provider probe on display tick"); return .openai },
            beginOperation: { XCTFail("No account mutation on display tick"); return false }, finishOperation: {},
            prepareSharedHistory: { XCTFail("No shared history refresh on display tick") }, showError: { _, _ in XCTFail("Unexpected error") },
            onChange: { rebuilds += 1 }, onIconChange: { _ in }, openWarmup: { XCTFail("No warmup on display tick") }, store: store)
        // Open before supplying cached usage: the isolated store has no auth,
        // so menuWillOpen cannot start any account CLI work.
        accounts.menuWillOpen()
        defer { accounts.menuDidClose(); accounts.stop() }
        let now = instant("2026-10-20T15:59:00Z")
        func snapshot(resetAt: Date?) -> UsageSnapshot {
            UsageSnapshot(planType: "plus", creditsRemaining: nil,
                primaryUsedPercent: 20, primaryResetAt: resetAt,
                secondaryUsedPercent: 30, secondaryResetAt: instant("2026-10-22T13:15:00Z"), fetchedAt: now,
                primaryWindowDurationMins: 300, secondaryWindowDurationMins: 10_080,
                resetCreditsAvailable: 1, nextResetCreditExpiresAt: instant("2026-10-23T14:45:00Z"))
        }
        store.updateStatus("fixture", .available(snapshot(resetAt: instant("2026-10-20T16:01:00Z"))))
        let menu = NSMenu()
        var probes = 0
        let connection = ConnectionMenuSection(refreshSharedHistoryItem: NSMenuItem(), onOpen: { probes += 1 })
        MainMenuBuilder.populate(menu: menu, connectionItem: connection.item,
            appendAccounts: { accounts.appendItems(to: $0, now: now) }, providerItems: [], trailingItems: [])
        let items = menu.items
        let tokenMenu = try XCTUnwrap(items.first { $0.title == "Token 用量详情" }?.submenu)
        accounts.menuWillOpen(tokenMenu)
        let tokenItems = tokenMenu.items
        let header = try XCTUnwrap(items.compactMap { $0.view as? NSHostingView<UsageHeaderView> }.first)
        let card = try XCTUnwrap(items.compactMap { $0.view as? NSHostingView<ProfileCardView> }.first)
        let height = card.frame.height
        XCTAssertEqual(ProfileCardView.primaryResetPresentation(status: card.rootView.status, now: card.rootView.now)?.text,
                       "明日00:01 重置 · 余2分")
        accounts.refreshDisplayedTimes(now: now.addingTimeInterval(60))
        XCTAssertEqual(header.rootView.now, now.addingTimeInterval(60))
        XCTAssertEqual(card.rootView.now, header.rootView.now)
        XCTAssertEqual(ProfileCardView.primaryResetPresentation(status: card.rootView.status, now: card.rootView.now)?.text,
                       "00:01 重置 · 余1分")
        store.updateStatus("fixture", .stale(snapshot(resetAt: instant("2026-10-20T16:05:00Z"))))
        accounts.refreshDisplayedTimes(now: now.addingTimeInterval(60))
        XCTAssertEqual(ProfileCardView.primaryResetPresentation(status: card.rootView.status, now: card.rootView.now)?.text,
                       "缓存00:05 · 余5分")
        store.updateStatus("fixture", .stale(snapshot(resetAt: nil)))
        accounts.refreshDisplayedTimes(now: now.addingTimeInterval(120))
        XCTAssertEqual(ProfileCardView.primaryResetPresentation(status: card.rootView.status, now: card.rootView.now)?.text,
                       "缓存·未返回重置时刻")
        XCTAssertEqual(card.frame.height, height)
        XCTAssertEqual(menu.items.count, items.count)
        XCTAssertTrue(zip(menu.items, items).allSatisfy { $0 === $1 })
        XCTAssertTrue(zip(menu.items.compactMap(\.view), items.compactMap(\.view)).allSatisfy { $0 === $1 })
        XCTAssertTrue(connection.item.submenu === menu.items.first?.submenu)
        XCTAssertTrue(tokenMenu === menu.items.first { $0.title == "Token 用量详情" }?.submenu)
        XCTAssertEqual(tokenItems.count, tokenMenu.items.count)
        XCTAssertTrue(zip(tokenItems, tokenMenu.items).allSatisfy { $0 === $1 })
        XCTAssertEqual(probes, 0)
        XCTAssertEqual(rebuilds, 0)
    }
}
