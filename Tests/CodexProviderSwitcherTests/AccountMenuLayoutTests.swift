import AppKit
import CodexProfileCore
import SwiftUI
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class AccountMenuLayoutTests: XCTestCase {
    func testMenuHostsFillParentWithoutWidthDrift() {
        let now = Date(timeIntervalSince1970: 1_792_483_800)
        let header = UsageHeaderView(isRefreshing: false, updatedAt: now,
            failingProfiles: 0, trackedProfiles: 1, now: now)
        let snapshot = UsageSnapshot(planType: "plus", creditsRemaining: nil,
            primaryUsedPercent: 20, primaryResetAt: now.addingTimeInterval(3600),
            secondaryUsedPercent: 40, secondaryResetAt: now.addingTimeInterval(86_400),
            fetchedAt: now, primaryWindowDurationMins: 300, secondaryWindowDurationMins: 10_080)
        let card = ProfileCardView(profile: .init(id: "fixture", label: "Synthetic account"),
            status: .available(snapshot), isActive: true, duplicateLine: nil, onSwitch: {}, now: now)
        let hosts: [NSView] = [
            AccountMenuLayout.makeHost(rootView: header, height: UsageHeaderView.baseHeight),
            AccountMenuLayout.makeHost(rootView: card, height: 78),
            AccountMenuLayout.makeHost(rootView: Color(nsColor: .separatorColor).frame(height: 1), height: 5),
        ]
        for host in hosts {
            let height = host.frame.height
            let parent = NSView(frame: NSRect(x: 0, y: 0, width: 340, height: height))
            parent.addSubview(host)
            XCTAssertTrue(host.autoresizingMask.contains(.width))
            for width: CGFloat in [340, 380, 420, 340, 420, 340] {
                parent.setFrameSize(NSSize(width: width, height: height))
                host.layoutSubtreeIfNeeded()
                XCTAssertEqual(host.frame.width, width, accuracy: 0.01)
                XCTAssertEqual(host.frame.height, height, accuracy: 0.01)
            }
        }
    }

    func testLayoutConstantsLeaveTheRequiredProgressBarWidthAtMinimum() {
        let contentWidth = AccountMenuLayout.minimumContentWidth
            - (AccountMenuLayout.horizontalPadding * 2)
        let fixedColumns = AccountMenuLayout.windowLabelWidth
            + AccountMenuLayout.percentageWidth
            + AccountMenuLayout.primaryTimeWidth
            + AccountMenuLayout.rowSpacing * 3
        XCTAssertEqual(contentWidth - fixedColumns, AccountMenuLayout.minimumBarWidth, accuracy: 0.01)
    }
}
