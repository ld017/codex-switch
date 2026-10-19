import AppKit
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class ConnectionMenuSectionTests: XCTestCase {
    func testUpdatesKeepAllTrackedMenuObjectsAndOnlyChangeOneParent() throws {
        let refresh = NSMenuItem(title: "刷新共享任务/项目视图...", action: nil, keyEquivalent: "")
        let section = ConnectionMenuSection(refreshSharedHistoryItem: refresh, onOpen: {})
        let parent = section.item, submenu = try XCTUnwrap(section.item.submenu)
        let children = submenu.items
        var s = ConnectionStatusSnapshot(); s.provider = .configured(.sub2api)
        s.sub2APIOnline = false; s.sub2APICheckedAt = Date()
        section.update(.make(snapshot: s, now: Date()))
        XCTAssertEqual(parent.title, "Sub2API · 端口检查未通过")
        XCTAssertNotNil(parent.image)
        s.sub2APIOnline = true
        section.update(.make(snapshot: s, now: Date()))
        XCTAssertEqual(parent.title, "连接：Sub2API")
        XCTAssertTrue(section.item === parent); XCTAssertTrue(section.item.submenu === submenu)
        XCTAssertEqual(children.count, 8)
        XCTAssertTrue(zip(children, submenu.items).allSatisfy { $0 === $1 })
        XCTAssertTrue(submenu.items.last === refresh)
    }
    func testSubmenuHasIndependentLifecycleAndRemainsInspectableWhileBusy() throws {
        var diagnosticOpens = 0
        let refresh = NSMenuItem(title: "刷新共享任务/项目视图...", action: nil, keyEquivalent: "")
        refresh.isEnabled = false
        let section = ConnectionMenuSection(refreshSharedHistoryItem: refresh, onOpen: { diagnosticOpens += 1 })
        let submenu = try XCTUnwrap(section.item.submenu)
        section.menuWillOpen(NSMenu())
        XCTAssertEqual(diagnosticOpens, 0)
        section.menuWillOpen(submenu); section.menuWillOpen(submenu)
        XCTAssertEqual(diagnosticOpens, 2)
        section.update(.make(snapshot: .init(provider: .unreadable), now: Date()))
        XCTAssertTrue(section.item.isEnabled)
        XCTAssertFalse(refresh.isEnabled)
        XCTAssertFalse(submenu.autoenablesItems)
        XCTAssertTrue(submenu.delegate === section)
    }
}
