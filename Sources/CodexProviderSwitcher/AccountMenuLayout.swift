import AppKit
import SwiftUI

@MainActor
enum AccountMenuLayout {
    static let minimumContentWidth: CGFloat = 340
    static let horizontalPadding: CGFloat = 10
    static let windowLabelWidth: CGFloat = 34
    static let percentageWidth: CGFloat = 30
    static let primaryTimeWidth: CGFloat = 144
    static let rowSpacing: CGFloat = 4
    static let minimumBarWidth: CGFloat = 100

    static func makeHost<Content: View>(rootView: Content, height: CGFloat) -> NSHostingView<Content> {
        let host = NSHostingView(rootView: rootView)
        host.frame = NSRect(x: 0, y: 0, width: self.minimumContentWidth, height: height)
        host.autoresizingMask = [.width]
        return host
    }
}
