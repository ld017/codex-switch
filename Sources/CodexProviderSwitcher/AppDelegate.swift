import AppKit
import Carbon
import CodexProfileCore
import CodexProviderCore
import Foundation

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    private let system = CodexSystem()
    private let loginItems = LoginItemManager()
    private let diagnosticLogger = SwitchDiagnosticLogger()
    private let enableLoginItemOnLaunch: Bool
    private lazy var accounts = AccountManager(
        currentProvider: { [weak self] in
            guard let self else { throw CodexSystemError.configMissing }
            return try self.system.currentProvider()
        },
        beginOperation: { [weak self] in
            guard let self else { return false }
            let started = self.switchActivity.begin()
            if started { self.refreshStatus() }
            return started
        },
        finishOperation: { [weak self] in
            self?.finishSwitching()
        },
        prepareSharedHistory: { [weak self] in
            guard let self else { throw CodexSystemError.sharedHistoryLaunchEnvironmentFailed }
            try await self.system.prepareSharedHistoryLaunch()
        },
        showError: { [weak self] error, title in
            self?.showError(error, title: title)
        },
        onChange: { [weak self] in
            self?.rebuildMenu()
        },
        onIconChange: { [weak self] image in
            self?.statusItem?.button?.image = image
        }
    )

    private var statusItem: NSStatusItem!
    private let menu = NSMenu()
    private let currentItem = NSMenuItem()
    private let serviceItem = NSMenuItem()
    private let proxyEnvironmentItem = NSMenuItem()
    private let sharedHistoryStatusItem = NSMenuItem()
    private let refreshSharedHistoryItem = NSMenuItem(
        title: "刷新共享任务/项目视图...",
        action: #selector(refreshSharedHistory),
        keyEquivalent: ""
    )
    private let openAIItem = NSMenuItem(
        title: "切换到 OpenAI 官方...",
        action: #selector(switchToOpenAI),
        keyEquivalent: ""
    )
    private let sub2APIItem = NSMenuItem(
        title: "切换到 Sub2API...",
        action: #selector(switchToSub2API),
        keyEquivalent: ""
    )
    private let loginItem = NSMenuItem(
        title: "登录时自动启动",
        action: #selector(toggleLoginItem),
        keyEquivalent: ""
    )
    private let quitItem = NSMenuItem(
        title: "退出菜单栏工具",
        action: #selector(quit),
        keyEquivalent: "q"
    )

    let switchActivity = SwitchActivity()
    private lazy var switchCoordinator = SwitchCoordinator(system: system)
    private lazy var switchPreparationCoordinator =
        SwitchPreparationCoordinator(system: system)
    private lazy var sharedHistoryRestartCoordinator =
        SharedHistoryRestartCoordinator(system: system)
    private var refreshTimer: Timer?
    private var pendingAction: SwitcherURLAction?

    private var switching: Bool {
        switchActivity.isActive
    }

    init(enableLoginItemOnLaunch: Bool = false) {
        self.enableLoginItemOnLaunch = enableLoginItemOnLaunch
        super.init()
    }

    func applicationWillFinishLaunching(_ notification: Notification) {
        NSAppleEventManager.shared().setEventHandler(
            self,
            andSelector: #selector(handleGetURLEvent(_:withReplyEvent:)),
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApplication.shared.setActivationPolicy(.accessory)
        configureStatusItem()
        if enableLoginItemOnLaunch || loginItems.isEnabled {
            do {
                try loginItems.enable()
            } catch {
                showError(error)
            }
        }
        refreshStatus()
        accounts.start()
        refreshTimer = Timer.scheduledTimer(
            withTimeInterval: 10,
            repeats: true
        ) { [weak self] _ in
            Task { @MainActor in
                self?.refreshStatus()
            }
        }
        if let action = pendingAction {
            pendingAction = nil
            handle(action)
        } else {
            Task {
                do {
                    try await system.prepareSharedHistoryLaunch()
                } catch {
                    showError(error, title: "共享任务/项目配置失败")
                }
                refreshStatus()
            }
        }
    }

    func applicationWillTerminate(_ notification: Notification) {
        refreshTimer?.invalidate()
        accounts.stop()
        NSAppleEventManager.shared().removeEventHandler(
            forEventClass: AEEventClass(kInternetEventClass),
            andEventID: AEEventID(kAEGetURL)
        )
    }

    func applicationShouldTerminate(
        _ sender: NSApplication
    ) -> NSApplication.TerminateReply {
        switching ? .terminateCancel : .terminateNow
    }

    func applicationShouldHandleReopen(
        _ sender: NSApplication,
        hasVisibleWindows flag: Bool
    ) -> Bool {
        statusItem.button?.performClick(nil)
        return true
    }

    func menuWillOpen(_ menu: NSMenu) {
        refreshStatus()
        accounts.menuWillOpen()
        rebuildMenu()
    }

    private func configureStatusItem() {
        statusItem = NSStatusBar.system.statusItem(
            withLength: NSStatusItem.squareLength
        )
        if let button = statusItem.button {
            button.image = IconRenderer.renderEmpty()
            button.imageScaling = .scaleNone
            button.toolTip = "Codex Switch"
        }

        currentItem.isEnabled = false
        serviceItem.isEnabled = false
        proxyEnvironmentItem.isEnabled = false
        sharedHistoryStatusItem.isEnabled = false
        openAIItem.target = self
        sub2APIItem.target = self
        refreshSharedHistoryItem.target = self
        loginItem.target = self

        let openCodexItem = NSMenuItem(
            title: "打开 Codex",
            action: #selector(openCodex),
            keyEquivalent: ""
        )
        openCodexItem.target = self

        quitItem.target = self

        menu.delegate = self
        statusItem.menu = menu
        rebuildMenu(openCodexItem: openCodexItem)
    }

    private func rebuildMenu(openCodexItem suppliedOpenCodexItem: NSMenuItem? = nil) {
        let openCodexItem = suppliedOpenCodexItem ?? NSMenuItem(
            title: "打开 Codex",
            action: #selector(openCodex),
            keyEquivalent: "")
        openCodexItem.target = self

        menu.removeAllItems()

        let title = NSMenuItem(title: "Codex Switch", action: nil, keyEquivalent: "")
        title.isEnabled = false
        title.image = NSImage(systemSymbolName: "arrow.triangle.2.circlepath", accessibilityDescription: nil)
        menu.addItem(title)
        menu.addItem(currentItem)
        menu.addItem(serviceItem)
        menu.addItem(proxyEnvironmentItem)
        menu.addItem(sharedHistoryStatusItem)
        menu.addItem(.separator())
        menu.addItem(openAIItem)
        menu.addItem(sub2APIItem)
        menu.addItem(refreshSharedHistoryItem)
        menu.addItem(.separator())

        accounts.appendItems(to: menu)

        menu.addItem(.separator())
        menu.addItem(openCodexItem)
        menu.addItem(loginItem)
        menu.addItem(quitItem)
    }

    private func refreshStatus() {
        do {
            let provider = try system.currentProvider()
            currentItem.title = "当前连接：\(provider.displayName)"
            openAIItem.state = provider == .openai ? .on : .off
            sub2APIItem.state = provider == .sub2api ? .on : .off
            openAIItem.isEnabled = !switching && provider != .openai
            sub2APIItem.isEnabled = !switching && provider != .sub2api
        } catch {
            currentItem.title = "当前连接：无法读取"
            openAIItem.state = .off
            sub2APIItem.state = .off
            openAIItem.isEnabled = !switching
            sub2APIItem.isEnabled = !switching
        }
        loginItem.state = loginItems.isEnabled ? .on : .off
        quitItem.isEnabled = !switching
        refreshSharedHistoryItem.isEnabled = !switching
        Task {
            let online = await system.sub2APIIsOnline()
            serviceItem.title = online
                ? "Sub2API 服务：在线"
                : "Sub2API 服务：离线"
        }
        Task {
            let conflicts = await system.proxyEnvironmentConflicts()
            if !conflicts.isEmpty {
                proxyEnvironmentItem.title = "代理状态：环境冲突"
            } else {
                let readiness = await system.clashReadinessStatus()
                proxyEnvironmentItem.title = "代理状态："
                    + readiness.userDescription
            }
        }
        Task {
            let configured = await system.sharedHistoryIsConfigured()
            sharedHistoryStatusItem.title = configured
                ? "共享任务/项目：已配置"
                : "共享任务/项目：待配置"
        }
    }

    @objc private func switchToOpenAI() {
        beginSwitch(to: .openai)
    }

    @objc private func switchToSub2API() {
        beginSwitch(to: .sub2api)
    }

    private func beginSwitch(to target: Provider) {
        guard !switching else {
            return
        }
        do {
            let current = try system.currentProvider()
            guard SwitchDecision.requiresSwitch(
                from: current,
                to: target
            ) else {
                notify(
                    title: "Codex 连接未变",
                    message: "当前已经是 \(target.displayName)"
                )
                return
            }
        } catch {
            showError(error)
            return
        }
        guard switchActivity.begin() else {
            return
        }
        refreshStatus()

        Task {
            defer {
                finishSwitching()
            }
            do {
                guard confirmSwitch(to: target) else {
                    return
                }
                try await switchPreparationCoordinator.prepare(
                    target: target
                )
                let result = try await switchCoordinator.execute(
                    target: target,
                    confirmForceQuit: { self.confirmForceQuit() }
                )
                guard result == .switched else {
                    return
                }
                notify(
                    title: "Codex 已切换",
                    message: target.displayName
                )
            } catch {
                showError(error)
            }
        }
    }

    private func finishSwitching() {
        switchActivity.finish()
        refreshStatus()
    }

    private func confirmSwitch(to target: Provider) -> Bool {
        let alert = NSAlert()
        alert.messageText = "切换到 \(target.displayName)？"
        alert.informativeText =
            "Codex 将正常退出并重新打开，正在运行的任务会被中断。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "切换并重启")
        alert.addButton(withTitle: "取消")
        NSApplication.shared.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    private func confirmForceQuit() -> Bool {
        let alert = NSAlert()
        alert.messageText = "Codex 未能正常退出"
        alert.informativeText =
            "可以强制退出后继续切换，或取消并恢复原配置。"
        alert.alertStyle = .critical
        alert.addButton(withTitle: "强制退出")
        alert.addButton(withTitle: "取消并恢复")
        NSApplication.shared.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    private func confirmSharedHistoryRestart() -> Bool {
        let alert = NSAlert()
        alert.messageText = "刷新共享任务/项目视图？"
        alert.informativeText =
            "保持当前连接，不切换提供方。Codex 将退出并重新打开，" +
            "正在运行的任务会被中断。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "保持当前连接并重启")
        alert.addButton(withTitle: "取消")
        NSApplication.shared.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    private func confirmSharedHistoryForceQuit() -> Bool {
        let alert = NSAlert()
        alert.messageText = "Codex 未能正常退出"
        alert.informativeText =
            "可以强制退出后继续刷新共享视图，或取消并保持当前连接。"
        alert.alertStyle = .critical
        alert.addButton(withTitle: "强制退出")
        alert.addButton(withTitle: "取消")
        NSApplication.shared.activate(ignoringOtherApps: true)
        return alert.runModal() == .alertFirstButtonReturn
    }

    private func showError(
        _ error: Error,
        title: String = "Codex 切换失败"
    ) {
        if let staged = error as? StagedSwitchError {
            diagnosticLogger.record(staged)
        }
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = error.localizedDescription
        alert.alertStyle = .critical
        alert.addButton(withTitle: "好")
        NSApplication.shared.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    private func notify(title: String, message: String) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
        process.arguments = [
            "-e",
            "display notification \"\(message)\" with title \"\(title)\"",
        ]
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        try? process.run()
    }

    @objc private func openCodex() {
        guard switchActivity.begin() else {
            return
        }
        refreshStatus()
        Task {
            defer {
                finishSwitching()
            }
            do {
                try await system.validateProxyEnvironment()
                try await system.prepareSharedHistoryLaunch()
                system.openCodex()
            } catch {
                showError(error, title: "Codex 打开失败")
            }
        }
    }

    @objc private func refreshSharedHistory() {
        beginSharedHistoryRestart()
    }

    private func beginSharedHistoryRestart() {
        guard !switching, confirmSharedHistoryRestart(),
              switchActivity.begin() else {
            return
        }
        refreshStatus()
        Task {
            defer {
                finishSwitching()
            }
            do {
                try await system.validateProxyEnvironment()
                let result = try await sharedHistoryRestartCoordinator.execute(
                    confirmForceQuit: {
                        self.confirmSharedHistoryForceQuit()
                    }
                )
                guard result == .restarted else {
                    return
                }
                notify(
                    title: "Codex 共享视图已刷新",
                    message: "当前连接保持不变"
                )
            } catch {
                showError(error, title: "共享任务/项目刷新失败")
            }
        }
    }

    @objc private func toggleLoginItem() {
        do {
            if loginItems.isEnabled {
                try loginItems.disable()
            } else {
                try loginItems.enable()
            }
        } catch {
            showError(error)
        }
        refreshStatus()
    }

    @objc private func quit() {
        guard !switching else {
            return
        }
        NSApplication.shared.terminate(nil)
    }

    @objc private func handleGetURLEvent(
        _ event: NSAppleEventDescriptor,
        withReplyEvent replyEvent: NSAppleEventDescriptor
    ) {
        guard let value = event.paramDescriptor(
            forKeyword: AEKeyword(keyDirectObject)
        )?.stringValue,
        let url = URL(string: value),
        let action = URLRouter.action(from: url) else {
            return
        }
        if statusItem == nil {
            pendingAction = action
        } else {
            handle(action)
        }
    }

    private func handle(_ action: SwitcherURLAction) {
        switch action {
        case .switchProvider(let provider):
            beginSwitch(to: provider)
        case .refreshSharedHistory:
            beginSharedHistoryRestart()
        }
    }
}
