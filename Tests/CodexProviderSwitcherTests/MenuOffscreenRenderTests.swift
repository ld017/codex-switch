import AppKit
import CodexProfileCore
import SwiftUI
import XCTest
@testable import CodexProviderSwitcher

@MainActor
final class MenuOffscreenRenderTests: XCTestCase {
    private let outputDirectory: URL = {
        if let path = ProcessInfo.processInfo.environment["CODEX_MENU_RENDER_OUTPUT_DIRECTORY"], !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }
        return FileManager.default.temporaryDirectory
            .appendingPathComponent("codex-menu-render-\(UUID().uuidString)", isDirectory: true)
    }()

    override func tearDown() {
        if ProcessInfo.processInfo.environment["CODEX_MENU_RENDER_OUTPUT_DIRECTORY"]?.isEmpty != false {
            try? FileManager.default.removeItem(at: self.outputDirectory)
        }
        super.tearDown()
    }

    private func instant(_ value: String) -> Date {
        ISO8601DateFormatter().date(from: value)!
    }

    private func render<V: View>(_ view: V, width: CGFloat, height: CGFloat, appearance: NSAppearance.Name, name: String) throws {
        let host = NSHostingView(rootView: view
            .background(Color(nsColor: .windowBackgroundColor))
            .environment(\.colorScheme, appearance == .darkAqua ? .dark : .light))
        host.frame = NSRect(x: 0, y: 0, width: width, height: height)
        host.appearance = NSAppearance(named: appearance)
        host.needsLayout = true
        host.layoutSubtreeIfNeeded()
        let bitmap = host.bitmapImageRepForCachingDisplay(in: host.bounds)
        XCTAssertNotNil(bitmap)
        guard let bitmap else { return }
        host.cacheDisplay(in: host.bounds, to: bitmap)
        XCTAssertGreaterThan(bitmap.pixelsWide, 0)
        XCTAssertGreaterThan(bitmap.pixelsHigh, 0)
        try FileManager.default.createDirectory(at: self.outputDirectory, withIntermediateDirectories: true)
        let url = self.outputDirectory.appendingPathComponent(name).appendingPathExtension("png")
        try XCTUnwrap(bitmap.representation(using: .png, properties: [:])).write(to: url)
    }

    private func snapshot(now: Date, resetAt: Date?) -> UsageSnapshot {
        UsageSnapshot(
            planType: "plus", creditsRemaining: 1234.5,
            primaryUsedPercent: 20, primaryResetAt: resetAt,
            secondaryUsedPercent: 60, secondaryResetAt: now.addingTimeInterval(3 * 86_400),
            fetchedAt: now, primaryWindowDurationMins: 300,
            secondaryWindowDurationMins: 10_080, resetCreditsAvailable: 2,
            nextResetCreditExpiresAt: now.addingTimeInterval(86_400))
    }

    private func card(label: String, now: Date, status: ProfileStatus? = nil, duplicate: String? = nil) -> ProfileCardView {
        return ProfileCardView(
            profile: ProfileConfig(id: label, label: label),
            status: status ?? .available(self.snapshot(now: now, resetAt: now.addingTimeInterval(4 * 3600 + 21 * 60))),
            isActive: true, duplicateLine: duplicate, onSwitch: {}, now: now)
    }

    func testProductionMenuComponentsRenderAtAllWidthsAndColorSchemes() throws {
        let now = instant("2026-10-20T08:10:00Z")
        for width in [CGFloat(340), 380, 420] {
            for (appearance, suffix) in [(NSAppearance.Name.aqua, "light"), (.darkAqua, "dark")] {
                let header = UsageHeaderView(isRefreshing: false, updatedAt: now,
                    failingProfiles: 0, trackedProfiles: 0,
                    tokenSummary: "Token 合计：未获取", tokenCoverage: "账号覆盖：0/0", now: now)
                try self.render(header, width: width, height: UsageHeaderView.baseHeight,
                    appearance: appearance, name: "header-\(Int(width))-\(suffix)")

                try self.render(self.card(label: "Synthetic account with a deliberately long label", now: now),
                    width: width, height: 93, appearance: appearance,
                    name: "card-one-\(Int(width))-\(suffix)")

                let cards = (0..<4).map { self.card(label: "Synthetic \($0 + 1)", now: now) }
                let stack = VStack(alignment: .leading, spacing: AccountMenuLayout.rowSpacing) {
                    ForEach(Array(cards.enumerated()), id: \.offset) { _, card in card.frame(height: 93) }
                }
                try self.render(stack, width: width, height: 4 * 93 + 3 * AccountMenuLayout.rowSpacing,
                    appearance: appearance, name: "cards-four-\(Int(width))-\(suffix)")

                let future = self.snapshot(now: now, resetAt: self.instant("2026-11-20T16:31:00Z"))
                let expired = self.snapshot(now: now, resetAt: now.addingTimeInterval(-86_400))
                let missing = self.snapshot(now: now, resetAt: nil)
                let states: [(String, ProfileStatus, CGFloat, String?)] = [
                    ("cached-long-date", .stale(future), 93, nil),
                    ("cached-expired", .stale(expired), 93, nil),
                    ("cached-missing", .stale(missing), 93, nil),
                    ("relogin-duplicate", .reloginNeeded(future), 119, "与 Synthetic 2 是同一账号"),
                    ("failed", .stale(nil), 68, nil),
                    ("setup", .notSetUp, 68, nil),
                ]
                for (name, status, height, duplicate) in states {
                    try self.render(self.card(label: "Synthetic \(name)", now: now, status: status, duplicate: duplicate),
                        width: width, height: height, appearance: appearance,
                        name: "state-\(name)-\(Int(width))-\(suffix)")
                }
            }
        }
    }
}
