import AppKit
import CodexProfileCore
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class CompactMenuIntegrationTests: XCTestCase {
    private func accountFixture() -> (URL, AccountManager) {
        let home = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
        let vault = FileAuthVault(root: paths.devAuthStoreURL, authLockURL: paths.authLockURL)
        let store = ProfileStore(authVault: vault, environment: paths.environment)
        let manager = AccountManager(currentProvider: { .openai }, beginOperation: { true },
            finishOperation: {}, prepareSharedHistory: {}, showError: { _, _ in },
            onChange: {}, onIconChange: { _ in }, openWarmup: {}, store: store)
        return (home, manager)
    }
    func testSingleConnectionEntryAccountsBeforeDirectSwitchActionsAndPreservedWarmup() throws {
        let (home, accounts) = accountFixture()
        defer { try? FileManager.default.removeItem(at: home) }
        let menu = NSMenu()
        let shared = NSMenuItem(title: "刷新共享任务/项目视图...", action: nil, keyEquivalent: "")
        let section = ConnectionMenuSection(refreshSharedHistoryItem: shared, onOpen: {})
        let official = NSMenuItem(title: "切换到 OpenAI 官方...", action: nil, keyEquivalent: "")
        let sub = NSMenuItem(title: "切换到 Sub2API...", action: nil, keyEquivalent: "")
        official.state = .on; official.isEnabled = false; sub.isEnabled = false
        let open = NSMenuItem(title: "打开 Codex", action: nil, keyEquivalent: "")
        MainMenuBuilder.populate(menu: menu, connectionItem: section.item,
            appendAccounts: { accounts.appendItems(to: $0) }, providerItems: [official, sub], trailingItems: [open])
        XCTAssertTrue(menu.items.first === section.item)
        XCTAssertTrue(menu.items[1].isSeparatorItem)
        let warmups = menu.items.filter { $0.title.hasPrefix("定时预热") }
        XCTAssertEqual(warmups.count, 1)
        let index = try XCTUnwrap(menu.items.firstIndex { $0 === official })
        XCTAssertLessThan(try XCTUnwrap(menu.items.firstIndex { $0 === warmups.first }), index)
        XCTAssertTrue(menu.items[index - 1].isSeparatorItem)
        XCTAssertTrue(menu.items[index + 1] === sub)
        XCTAssertEqual(menu.items.filter { $0 === official || $0 === sub }.count, 2)
        XCTAssertFalse(menu.items.contains { $0 === shared })
        XCTAssertEqual(section.item.submenu?.items.filter { $0 === shared }.count, 1)
        XCTAssertFalse(menu.items.contains { ["Codex Switch", "当前连接：", "Sub2API 服务：", "代理状态：", "共享任务/项目："].contains(where: $0.title.hasPrefix) })
        XCTAssertEqual(official.state, .on); XCTAssertFalse(official.isEnabled); XCTAssertFalse(sub.isEnabled)
        XCTAssertFalse(menu.autoenablesItems)
        XCTAssertTrue(section.item.isEnabled)
        XCTAssertTrue(menu.items.last === open)
    }
    func testEmptyAccountSectionHasNoLeadingTrailingOrAdjacentSeparators() {
        let menu = NSMenu()
        let connection = NSMenuItem(title: "连接：读取中…", action: nil, keyEquivalent: "")
        MainMenuBuilder.populate(menu: menu, connectionItem: connection,
            appendAccounts: { $0.addItem(.separator()) }, providerItems: [], trailingItems: [])
        XCTAssertEqual(menu.items.count, 1)
        let action = NSMenuItem(title: "切换", action: nil, keyEquivalent: "")
        MainMenuBuilder.populate(menu: menu, connectionItem: connection,
            appendAccounts: { _ in }, providerItems: [action], trailingItems: [])
        XCTAssertEqual(menu.items.count, 3)
        XCTAssertFalse(menu.items.first!.isSeparatorItem); XCTAssertFalse(menu.items.last!.isSeparatorItem)
        XCTAssertFalse(zip(menu.items, menu.items.dropFirst()).contains { $0.isSeparatorItem && $1.isSeparatorItem })
    }
    func testDiagnosticUpdatesAndOpeningCannotRebuildRootOrAccountTokenItems() throws {
        let menu = NSMenu(), token = NSMenuItem(title: "Token 明细", action: nil, keyEquivalent: "")
        token.submenu = NSMenu()
        let tokenMenu = token.submenu
        var accountAppends = 0, opens = 0
        let section = ConnectionMenuSection(refreshSharedHistoryItem: NSMenuItem(), onOpen: { opens += 1 })
        MainMenuBuilder.populate(menu: menu, connectionItem: section.item, appendAccounts: {
            accountAppends += 1; $0.addItem(token)
        }, providerItems: [], trailingItems: [])
        let rootItems = menu.items
        for _ in 0..<10 {
            section.menuWillOpen(try XCTUnwrap(section.item.submenu))
            section.update(.make(snapshot: .init(provider: .unreadable), now: Date()))
        }
        XCTAssertEqual(opens, 10); XCTAssertEqual(accountAppends, 1)
        XCTAssertTrue(zip(rootItems, menu.items).allSatisfy { $0 === $1 })
        XCTAssertTrue(token.submenu === tokenMenu)
    }
}
