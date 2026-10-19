import AppKit

@MainActor
enum MainMenuBuilder {
    static func populate(
        menu: NSMenu,
        connectionItem: NSMenuItem,
        appendAccounts: (NSMenu) -> Void,
        providerItems: [NSMenuItem],
        trailingItems: [NSMenuItem]
    ) {
        menu.removeAllItems()
        // Enabled state is owned by the operation gate, including while the
        // connection submenu is open. AppKit must not re-enable busy actions.
        menu.autoenablesItems = false
        menu.addItem(connectionItem)
        menu.addItem(.separator())
        appendAccounts(menu)
        menu.addItem(.separator())
        providerItems.forEach { menu.addItem($0) }
        trailingItems.forEach { menu.addItem($0) }
        // Account fixtures may be empty or include their own group separators.
        var previousWasSeparator = true
        for item in menu.items {
            if item.isSeparatorItem && previousWasSeparator { menu.removeItem(item) }
            else { previousWasSeparator = item.isSeparatorItem }
        }
        if let last = menu.items.last, last.isSeparatorItem { menu.removeItem(last) }
    }
}
