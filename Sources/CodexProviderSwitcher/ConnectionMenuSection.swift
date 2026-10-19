import AppKit

@MainActor
final class ConnectionMenuSection: NSObject, NSMenuDelegate {
    let item = NSMenuItem()
    private let submenu = NSMenu()
    private var detailItems: [ConnectionStatusPresentation.DetailRow.ID: NSMenuItem] = [:]
    private let onOpen: () -> Void

    init(refreshSharedHistoryItem: NSMenuItem, onOpen: @escaping () -> Void) {
        self.onOpen = onOpen
        super.init()
        submenu.autoenablesItems = false
        submenu.delegate = self
        for id in ConnectionStatusPresentation.DetailRow.ID.allCases {
            let detail = NSMenuItem()
            detail.isEnabled = false
            detailItems[id] = detail
            submenu.addItem(detail)
        }
        submenu.addItem(.separator())
        submenu.addItem(refreshSharedHistoryItem)
        item.submenu = submenu
        update(.make(snapshot: .init(), now: Date()))
    }

    func update(_ presentation: ConnectionStatusPresentation) {
        item.title = presentation.title
        item.image = NSImage(systemSymbolName: presentation.emphasis == .attention
            ? "exclamationmark.triangle" : "network", accessibilityDescription: nil)
        item.isEnabled = true
        for row in presentation.details { detailItems[row.id]?.title = row.title }
    }

    func menuWillOpen(_ menu: NSMenu) {
        guard menu === submenu else { return }
        onOpen()
    }
}
