import AppKit
import CodexProfileCore
import CodexProviderCore
import SwiftUI

@MainActor
final class AccountManager: NSObject {
    private let store: ProfileStore
    private let usageProvider: UsageProvider
    private let currentProvider: () throws -> Provider
    private let beginOperation: () -> Bool
    private let finishOperation: () -> Void
    private let prepareSharedHistory: () async throws -> Void
    private let showError: (Error, String) -> Void
    private let onChange: () -> Void
    private let onIconChange: (NSImage) -> Void

    private weak var menu: NSMenu?
    private var refreshTimer: Timer?
    private var lastLiveAuthMtime: Date?
    private var liveAuthWarning: LiveAuthWarning?

    init(
        currentProvider: @escaping () throws -> Provider,
        beginOperation: @escaping () -> Bool,
        finishOperation: @escaping () -> Void,
        prepareSharedHistory: @escaping () async throws -> Void,
        showError: @escaping (Error, String) -> Void,
        onChange: @escaping () -> Void,
        onIconChange: @escaping (NSImage) -> Void
    ) {
        self.store = ProfileStore()
        self.usageProvider = UsageProvider(store: self.store)
        self.currentProvider = currentProvider
        self.beginOperation = beginOperation
        self.finishOperation = finishOperation
        self.prepareSharedHistory = prepareSharedHistory
        self.showError = showError
        self.onChange = onChange
        self.onIconChange = onIconChange
        super.init()
    }

    deinit {
        self.refreshTimer?.invalidate()
    }

    func start() {
        CoreLogger.configure { level, message, metadata in
            switch level {
            case .info:
                AppLogger.info(message, metadata: metadata)
            case .warning:
                AppLogger.warning(message, metadata: metadata)
            case .error:
                AppLogger.error(message, metadata: metadata)
            }
        }
        self.syncActiveProfile(force: true)
        self.usageProvider.onRefreshComplete = { [weak self] in
            guard let self else { return }
            self.updateIcon()
            self.onChange()
        }
        self.requestRefresh(force: true)
        self.refreshTimer = Timer.scheduledTimer(withTimeInterval: 300, repeats: true) {
            [weak self] _ in
            Task { @MainActor in
                self?.syncActiveProfile(force: true)
                self?.requestRefresh(force: true)
            }
        }
    }

    func stop() {
        self.refreshTimer?.invalidate()
        self.refreshTimer = nil
        self.usageProvider.cancelRefreshes()
    }

    func menuWillOpen() {
        self.syncActiveProfile()
        self.requestRefresh()
    }

    func appendItems(to menu: NSMenu) {
        self.menu = menu

        if let warning = self.liveAuthWarning {
            let item = NSMenuItem(title: self.warningTitle(warning), action: nil, keyEquivalent: "")
            item.isEnabled = false
            item.image = NSImage(
                systemSymbolName: "exclamationmark.triangle.fill",
                accessibilityDescription: nil)
            menu.addItem(item)
        }

        self.addUsageHeader(to: menu)

        let records = ProfileHealth.build(
            profiles: self.store.config.profiles,
            statuses: self.store.statuses,
            activeProfileId: self.store.liveProfileId,
            canActivateAuth: { self.store.authCanBeActivated(for: $0) })

        for (index, health) in self.orderedRecords(records).enumerated() {
            self.addProfileCard(for: health, to: menu)
            if index != records.indices.last {
                self.addProfileDivider(to: menu)
            }
        }

        menu.addItem(.separator())

        let refresh = NSMenuItem(
            title: self.usageProvider.isRefreshing ? "正在刷新账号额度…" : "刷新账号额度",
            action: #selector(self.refreshAll),
            keyEquivalent: "r")
        refresh.keyEquivalentModifierMask = [.command]
        refresh.target = self
        refresh.isEnabled = !self.usageProvider.isRefreshing
        refresh.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: nil)
        menu.addItem(refresh)

        if self.liveAuthWarning == .unmanaged || self.savedProfileCount == 0 {
            let saveCurrent = NSMenuItem(
                title: "保存当前 OpenAI 登录…",
                action: #selector(self.saveCurrentLogin),
                keyEquivalent: "")
            saveCurrent.target = self
            saveCurrent.image = NSImage(systemSymbolName: "tray.and.arrow.down", accessibilityDescription: nil)
            menu.addItem(saveCurrent)
        }

        let add = NSMenuItem(
            title: "添加 OpenAI 账号…",
            action: #selector(self.addProfile),
            keyEquivalent: "")
        add.target = self
        add.image = NSImage(systemSymbolName: "person.badge.plus", accessibilityDescription: nil)
        menu.addItem(add)

        let manage = NSMenuItem(
            title: "管理 OpenAI 账号…",
            action: #selector(self.manageProfiles),
            keyEquivalent: "")
        manage.target = self
        manage.image = NSImage(systemSymbolName: "person.2", accessibilityDescription: nil)
        menu.addItem(manage)
    }

    private var savedProfileCount: Int {
        self.store.config.profiles.filter { self.store.authStoreExists(for: $0.id) }.count
    }

    private func orderedRecords(_ records: [ProfileHealth]) -> [ProfileHealth] {
        let active = records.filter(\.isActive)
        return active + ProfileHealth.menuOrderedInactive(records)
    }

    private func addUsageHeader(to menu: NSMenu) {
        let updatedAt = self.store.statuses.values.compactMap { $0.snapshot?.fetchedAt }.max()
        let tracked = self.store.config.profiles.filter {
            if case .notSetUp = self.store.statuses[$0.id] ?? .notSetUp { return false }
            return true
        }
        let failing = tracked.filter { self.store.refreshDiagnostics[$0.id]?.lastError != nil }
        let header = UsageHeaderView(
            isRefreshing: self.usageProvider.isRefreshing,
            updatedAt: updatedAt,
            failingProfiles: failing.count,
            trackedProfiles: tracked.count)
        let host = NSHostingView(rootView: header)
        host.frame = NSRect(
            x: 0,
            y: 0,
            width: 290,
            height: header.showsFailure ? UsageHeaderView.failureHeight : UsageHeaderView.baseHeight)
        let item = NSMenuItem()
        item.view = host
        menu.addItem(item)
    }

    private func addProfileCard(for health: ProfileHealth, to menu: NSMenu) {
        let duplicateLine = self.duplicateSummary(for: health.profile.id)
        let card = ProfileCardView(
            profile: health.profile,
            status: health.status,
            isActive: health.isActive,
            duplicateLine: duplicateLine,
            onSwitch: { [weak self] in self?.selectProfile(health.profile.id) })
        let host = NSHostingView(rootView: card)
        host.frame = NSRect(
            x: 0,
            y: 0,
            width: 290,
            height: self.cardHeight(for: health.status, hasDuplicate: duplicateLine != nil))
        let item = NSMenuItem()
        item.view = host
        menu.addItem(item)
    }

    private func addProfileDivider(to menu: NSMenu) {
        let divider = Color(nsColor: .separatorColor)
            .frame(height: 1)
            .padding(.horizontal, 14)
        let host = NSHostingView(rootView: divider)
        host.frame = NSRect(x: 0, y: 0, width: 290, height: 5)
        let item = NSMenuItem()
        item.view = host
        menu.addItem(item)
    }

    private func cardHeight(for status: ProfileStatus, hasDuplicate: Bool) -> CGFloat {
        var height: CGFloat
        switch status {
        case .available, .stale(.some):
            height = 46
        case .reloginNeeded(.some):
            height = 58
        default:
            height = 36
        }
        if status.snapshot?.resetCreditsAvailable != nil { height += 15 }
        if hasDuplicate { height += 14 }
        return height
    }

    private func duplicateSummary(for profileID: String) -> String? {
        let duplicates = self.store.duplicateProfileIDs(for: profileID)
        guard !duplicates.isEmpty else { return nil }
        let names = duplicates.map { duplicateID in
            self.store.config.profiles.first(where: { $0.id == duplicateID })?.label
                ?? "账号 \(duplicateID)"
        }
        return "与 \(names.joined(separator: "、")) 是同一账号"
    }

    private func warningTitle(_ warning: LiveAuthWarning) -> String {
        switch warning {
        case .unmanaged:
            return "当前 OpenAI 登录尚未保存"
        case .ambiguous:
            return "当前登录与多个已保存账号重复"
        }
    }

    private func selectProfile(_ id: String) {
        self.menu?.cancelTracking()
        let status = self.store.statuses[id] ?? .notSetUp
        if case .notSetUp = status {
            self.startLogin(for: id)
            return
        }
        if case .reloginNeeded = status, !self.store.authCanBeActivated(for: id) {
            self.startLogin(for: id)
            return
        }
        guard id != self.store.liveProfileId else { return }
        guard self.store.authCanBeActivated(for: id) else {
            self.startLogin(for: id)
            return
        }

        do {
            guard try self.currentProvider() == .openai else {
                self.presentProviderRequirement()
                return
            }
        } catch {
            self.showError(error, "无法读取当前连接")
            return
        }

        let label = self.store.config.profiles.first(where: { $0.id == id })?.label ?? "账号 \(id)"
        let alert = NSAlert()
        alert.messageText = "切换到 \(label)？"
        alert.informativeText = "Codex 将退出并重新打开，正在运行的任务会被中断。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "切换并重启")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn, self.beginOperation() else { return }

        let store = self.store
        let workspace = store.relaunchWorkspacePath()
        store.beginAuthMutation()
        Task { @MainActor [weak self] in
            guard let self else {
                store.endAuthMutation()
                return
            }
            defer {
                store.endAuthMutation()
                self.finishOperation()
            }
            do {
                try await self.prepareSharedHistory()
            } catch {
                self.showError(error, "账号切换失败")
                return
            }
            let result = await CodexBridge.switchToProfile(id, workspacePath: workspace) {
                try store.prepareProfileSwitch(
                    to: id,
                    isCodexDesktopRunning: CodexBridge.isCodexDesktopRunning)
            }
            switch result {
            case .success:
                store.setActiveProfile(id)
                store.setLiveProfileId(id)
                self.liveAuthWarning = nil
                self.requestRefresh(force: true)
                self.updateIcon()
                self.onChange()
            case .failure(let error):
                self.syncActiveProfile(force: true)
                self.showError(error, "账号切换失败")
            }
        }
    }

    private func presentProviderRequirement() {
        let alert = NSAlert()
        alert.messageText = "请先切换到 OpenAI 官方"
        alert.informativeText =
            "账号切换只改变 OpenAI 登录。当前连接不是 OpenAI，先在菜单上方切换提供方，可避免同时修改两类状态。"
        alert.alertStyle = .informational
        alert.addButton(withTitle: "好")
        NSApp.activate(ignoringOtherApps: true)
        alert.runModal()
    }

    @objc private func refreshAll() {
        self.syncActiveProfile(force: true)
        self.requestRefresh(force: true)
        self.onChange()
    }

    @objc private func addProfile() {
        self.menu?.cancelTracking()
        let profile = self.store.addProfile()
        guard let label = self.promptForLabel(defaultValue: profile.label, title: "添加 OpenAI 账号") else {
            try? self.store.removeProfile(profile.id)
            self.onChange()
            return
        }
        self.store.updateLabel(for: profile.id, label: label)
        self.onChange()
        self.startLogin(for: profile.id)
    }

    @objc private func saveCurrentLogin() {
        self.menu?.cancelTracking()
        let target = self.store.config.profiles.first(where: {
            !self.store.authStoreExists(for: $0.id)
        }) ?? self.store.addProfile()
        guard let label = self.promptForLabel(defaultValue: target.label, title: "保存当前 OpenAI 登录") else {
            return
        }
        do {
            try self.store.importLiveAuth(to: target.id)
            self.store.updateLabel(for: target.id, label: label)
            self.syncActiveProfile(force: true)
            self.requestRefresh(force: true)
            self.onChange()
        } catch {
            self.showError(error, "保存当前登录失败")
        }
    }

    @objc private func manageProfiles() {
        self.menu?.cancelTracking()
        let profiles = self.store.config.profiles
        guard !profiles.isEmpty else { return }

        let selector = NSPopUpButton(frame: NSRect(x: 0, y: 0, width: 280, height: 26))
        profiles.forEach { selector.addItem(withTitle: $0.label) }

        let alert = NSAlert()
        alert.messageText = "管理 OpenAI 账号"
        alert.informativeText = "选择账号后执行一项操作。"
        alert.accessoryView = selector
        alert.addButton(withTitle: "重命名")
        alert.addButton(withTitle: "重新登录")
        alert.addButton(withTitle: "移除")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        let response = alert.runModal()
        guard profiles.indices.contains(selector.indexOfSelectedItem) else { return }
        let profile = profiles[selector.indexOfSelectedItem]

        switch response {
        case .alertFirstButtonReturn:
            guard let label = self.promptForLabel(defaultValue: profile.label, title: "重命名账号") else {
                return
            }
            self.store.updateLabel(for: profile.id, label: label)
            self.onChange()
        case .alertSecondButtonReturn:
            self.startLogin(for: profile.id)
        case .alertThirdButtonReturn:
            self.removeProfile(profile)
        default:
            return
        }
    }

    private func removeProfile(_ profile: ProfileConfig) {
        let alert = NSAlert()
        alert.messageText = "移除“\(profile.label)”？"
        alert.informativeText = "将删除该账号在 Codex Switch 中保存的登录副本和额度缓存，不会删除账号本身。"
        alert.alertStyle = .warning
        alert.addButton(withTitle: "移除")
        alert.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        do {
            try self.store.removeProfile(profile.id)
            self.onChange()
        } catch {
            self.showError(error, "无法移除账号")
        }
    }

    private func promptForLabel(defaultValue: String, title: String) -> String? {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = "输入一个便于识别的名称；登录凭据不会显示在菜单中。"
        alert.addButton(withTitle: "继续")
        alert.addButton(withTitle: "取消")
        let field = NSTextField(string: defaultValue)
        field.frame = NSRect(x: 0, y: 0, width: 280, height: 24)
        alert.accessoryView = field
        NSApp.activate(ignoringOtherApps: true)
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        let value = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? defaultValue : value
    }

    private func startLogin(for id: String) {
        let store = self.store
        store.beginAuthMutation()
        CodexBridge.startLogin(profileId: id) { [weak self] result in
            guard let self else {
                store.endAuthMutation()
                return
            }
            store.endAuthMutation()
            switch result {
            case .success:
                self.syncActiveProfile(force: true)
                self.requestRefresh(force: true)
                self.onChange()
            case .failure(let error):
                if !error.isUserCancelled {
                    self.showError(error, "账号登录失败")
                }
            }
        }
    }

    private func requestRefresh(force: Bool = false) {
        guard !self.usageProvider.isRefreshing else { return }
        self.usageProvider.refreshAll(force: force)
    }

    private func updateIcon() {
        guard let active = self.store.liveProfileId,
              let snapshot = self.store.statuses[active]?.snapshot else {
            self.onIconChange(IconRenderer.renderEmpty())
            return
        }
        self.onIconChange(IconRenderer.render(
            primaryPercent: snapshot.primaryUsedPercent,
            secondaryPercent: snapshot.secondaryUsedPercent))
    }

    private func syncActiveProfile(force: Bool = false) {
        guard !self.store.isAuthMutationInProgress() else { return }
        if !force {
            let mtime = self.store.liveAuthModificationDate()
            if mtime == self.lastLiveAuthMtime { return }
            self.lastLiveAuthMtime = mtime
        } else {
            self.lastLiveAuthMtime = nil
        }

        let matches = self.store.matchingProfilesForLiveAuth()
        if matches.count == 1, let match = matches.first {
            self.store.setLiveProfileId(match)
            self.store.setActiveProfile(match)
            self.liveAuthWarning = nil
            return
        }
        if matches.count > 1 {
            let preferred = self.store.liveProfileId ?? self.store.config.activeProfile
            if matches.contains(preferred) {
                self.store.setLiveProfileId(preferred)
                self.liveAuthWarning = nil
            } else {
                self.store.setLiveProfileId(nil)
                self.liveAuthWarning = .ambiguous
            }
            return
        }
        self.store.setLiveProfileId(nil)
        self.liveAuthWarning = self.store.liveAuthExists() ? .unmanaged : nil
    }
}
