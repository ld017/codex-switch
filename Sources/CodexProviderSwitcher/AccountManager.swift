import AppKit
import CodexProfileCore
import CodexProviderCore
import SwiftUI

@MainActor
final class AccountManager: NSObject, NSMenuDelegate {
    private let store: ProfileStore
    private let usageProvider: UsageProvider
    private let currentProvider: () throws -> Provider
    private let beginOperation: () -> Bool
    private let finishOperation: () -> Void
    private let prepareSharedHistory: () async throws -> Void
    private let showError: (Error, String) -> Void
    private let onChange: () -> Void
    private let onIconChange: (NSImage) -> Void
    private let openWarmup: () -> Void
    var warmupTitle: (() -> String)?
    var beforeAccountMutation: (() async throws -> Void)?
    var hasAuthMutationInFlight: Bool { self.store.isAuthMutationInProgress() }

    private weak var menu: NSMenu?
    private var refreshTimer: Timer?
    private var isMenuOpen = false
    private var headerHost: NSHostingView<UsageHeaderView>?
    private var profileHosts: [String: NSHostingView<ProfileCardView>] = [:]
    private weak var refreshMenuItem: NSMenuItem?
    private weak var tokenDetailsMenu: NSMenu?
    private var tokenMonthRows: [(activity: TokenActivity, value: NSMenuItem, through: NSMenuItem)] = []
    private var lastLiveAuthMtime: Date?
    private var liveAuthWarning: LiveAuthWarning?

    init(
        currentProvider: @escaping () throws -> Provider,
        beginOperation: @escaping () -> Bool,
        finishOperation: @escaping () -> Void,
        prepareSharedHistory: @escaping () async throws -> Void,
        showError: @escaping (Error, String) -> Void,
        onChange: @escaping () -> Void,
        onIconChange: @escaping (NSImage) -> Void,
        openWarmup: @escaping () -> Void,
        store: ProfileStore? = nil
    ) {
        self.store = store ?? ProfileStore()
        self.usageProvider = UsageProvider(store: self.store)
        self.currentProvider = currentProvider
        self.beginOperation = beginOperation
        self.finishOperation = finishOperation
        self.prepareSharedHistory = prepareSharedHistory
        self.showError = showError
        self.onChange = onChange
        self.onIconChange = onIconChange
        self.openWarmup = openWarmup
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
        self.usageProvider.onChange = { [weak self] in
            guard let self else { return }
            self.updateIcon()
            self.refreshVisibleItems()
        }
        self.requestRefresh(force: true)
        let timer = Timer(timeInterval: 300, repeats: true) {
            [weak self] _ in
            Task { @MainActor in
                self?.syncActiveProfile(force: true)
                self?.requestRefresh()
            }
        }
        timer.tolerance = 30
        RunLoop.main.add(timer, forMode: .common)
        self.refreshTimer = timer
    }

    func stop() {
        self.refreshTimer?.invalidate()
        self.refreshTimer = nil
        self.usageProvider.cancelRefreshes()
    }

    func menuWillOpen() {
        self.isMenuOpen = true
        self.syncActiveProfile()
        self.requestRefresh()
    }

    func menuDidClose() {
        self.isMenuOpen = false
    }

    // Token detail rows are created only when that submenu is opened.
    func menuWillOpen(_ menu: NSMenu) {
        guard menu === self.tokenDetailsMenu else { return }
        self.populateTokenDetails(menu)
    }

    func appendItems(to menu: NSMenu, now: Date = Date()) {
        self.menu = menu
        self.headerHost = nil
        self.profileHosts.removeAll()

        if let warning = self.liveAuthWarning {
            let item = NSMenuItem(title: self.warningTitle(warning), action: nil, keyEquivalent: "")
            item.isEnabled = false
            item.image = NSImage(
                systemSymbolName: "exclamationmark.triangle.fill",
                accessibilityDescription: nil)
            menu.addItem(item)
        }

        self.addUsageHeader(to: menu, now: now)

        let records = ProfileHealth.build(
            profiles: self.store.config.profiles,
            statuses: self.store.statuses,
            activeProfileId: self.store.liveProfileId,
            canActivateAuth: { self.store.authCanBeActivated(for: $0) })

        for (index, health) in self.orderedRecords(records).enumerated() {
            self.addProfileCard(for: health, to: menu, now: now)
            if index != records.indices.last {
                self.addProfileDivider(to: menu)
            }
        }

        self.addTokenDetails(to: menu)

        menu.addItem(.separator())

        let refresh = NSMenuItem(
            title: self.usageProvider.isRefreshing ? "正在刷新账号额度…" : "刷新账号额度",
            action: #selector(self.refreshAll),
            keyEquivalent: "r")
        refresh.keyEquivalentModifierMask = [.command]
        refresh.target = self
        refresh.isEnabled = true
        self.refreshMenuItem = refresh
        refresh.image = NSImage(systemSymbolName: "arrow.clockwise", accessibilityDescription: nil)
        menu.addItem(refresh)

        let warmup = NSMenuItem(
            title: self.warmupTitle?() ?? "定时预热…",
            action: #selector(self.openWarmupWindow),
            keyEquivalent: "")
        warmup.target = self
        warmup.image = NSImage(systemSymbolName: "clock", accessibilityDescription: nil)
        menu.addItem(warmup)

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

    private func usageHeader(now: Date = Date()) -> UsageHeaderView {
        let updatedAt = self.store.statuses.values.compactMap { $0.snapshot?.fetchedAt }.max()
        let tracked = self.store.config.profiles.filter {
            if case .notSetUp = self.store.statuses[$0.id] ?? .notSetUp { return false }
            return true
        }
        let failing = tracked.filter { self.store.refreshDiagnostics[$0.id]?.lastError != nil }
        let tokenSummary = TokenUsagePresentation.summary(tracked.map { profile in
            let status = self.store.statuses[profile.id] ?? .notSetUp
            return .init(identity: self.store.savedAuthFingerprint(for: profile.id),
                         activity: status.snapshot?.tokenActivity,
                         cached: TokenUsagePresentation.isCached(status))
        }, now: now)
        let header = UsageHeaderView(
            isRefreshing: self.usageProvider.isRefreshing,
            updatedAt: updatedAt,
            failingProfiles: failing.count,
            trackedProfiles: tracked.count,
            tokenSummary: tokenSummary.values,
            tokenCoverage: tokenSummary.coverage,
            refreshProgress: "正在刷新：\(self.usageProvider.completedProfiles)/\(self.usageProvider.totalProfiles) 个账号", now: now)
        return header
    }

    private func addUsageHeader(to menu: NSMenu, now: Date) {
        let header = self.usageHeader(now: now)
        let host = AccountMenuLayout.makeHost(rootView: header, height: header.showsFailure ? UsageHeaderView.failureHeight : UsageHeaderView.baseHeight)
        self.headerHost = host
        let item = NSMenuItem()
        item.view = host
        menu.addItem(item)
    }

    private func addProfileCard(for health: ProfileHealth, to menu: NSMenu, now: Date) {
        let duplicateLine = self.duplicateSummary(for: health.profile.id)
        let card = ProfileCardView(
            profile: health.profile,
            status: health.status,
            isActive: health.isActive,
            duplicateLine: duplicateLine,
            onSwitch: { [weak self] in self?.selectProfile(health.profile.id) }, now: now)
        let host = AccountMenuLayout.makeHost(
            rootView: card,
            height: self.cardHeight(for: health.status, hasDuplicate: duplicateLine != nil))
        self.profileHosts[health.profile.id] = host
        let item = NSMenuItem()
        item.view = host
        menu.addItem(item)
    }

    private func addProfileDivider(to menu: NSMenu) {
        let divider = Color(nsColor: .separatorColor)
            .frame(height: 1)
            .padding(.horizontal, 14)
        let host = AccountMenuLayout.makeHost(rootView: divider, height: 5)
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
        return height + 32
    }

    private func addTokenDetails(to menu: NSMenu) {
        tokenMonthRows.removeAll()
        let root = NSMenuItem(title: "Token 用量详情", action: nil, keyEquivalent: "")
        let details = NSMenu()
        details.addItem(NSMenuItem(title: "打开后加载详情", action: nil, keyEquivalent: ""))
        details.delegate = self
        self.tokenDetailsMenu = details
        root.submenu = details
        menu.addItem(root)
    }

    private func populateTokenDetails(_ details: NSMenu) {
        details.removeAllItems()
        tokenMonthRows.removeAll()
        @discardableResult
        func note(_ title: String, in target: NSMenu) -> NSMenuItem {
            let item = NSMenuItem(title: title, action: nil, keyEquivalent: "")
            item.isEnabled = false
            target.addItem(item)
            return item
        }
        note("官方 Codex 统计，可能延迟更新", in: details)
        note("服务端日桶保留原日期；缺项不代表零", in: details)
        note("本月：按北京日期筛选服务端日记录", in: details)
        for profile in self.store.config.profiles {
            let item = NSMenuItem(title: profile.label, action: nil, keyEquivalent: "")
            let account = NSMenu()
            let status = self.store.statuses[profile.id] ?? .notSetUp
            if let error = status.snapshot?.tokenActivityError {
                note("Token 统计读取失败，保留上次记录", in: account)
                note(LogRedactor.excerpt(error, maxLength: 160), in: account)
            }
            if let activity = status.snapshot?.tokenActivity {
                if let total = activity.lifetimeTokens {
                    note("累计：\(TokenUsagePresentation.exact(total)) Token"
                         + (TokenUsagePresentation.isCached(status) ? "（缓存）" : ""), in: account)
                } else {
                    note("累计：未获取", in: account)
                }
                note("读取于：\(DateFormatter.profileDetailTimestamp.string(from: activity.fetchedAt))", in: account)
                let value = note("本月：未获取（不代表零消耗）", in: account)
                let through = note("服务端未返回本月日记录", in: account)
                tokenMonthRows.append((activity, value, through))
                let days = activity.dailyUsageBuckets ?? []
                for day in days.sorted(by: { $0.startDate > $1.startDate }) {
                    note("\(day.startDate)：\(TokenUsagePresentation.millions(day.tokens)) Token", in: account)
                }
            } else {
                note(TokenUsagePresentation.cardLabel(status), in: account)
            }
            item.submenu = account
            details.addItem(item)
        }
        refreshTokenMonthRows(now: Date())
    }

    private func refreshTokenMonthRows(now: Date) {
        for row in tokenMonthRows {
            if let month = TokenUsagePresentation.monthUsage(row.activity, now: now) {
                row.value.title = "本月：\(TokenUsagePresentation.hundredMillions(month.tokens)) Token"
                row.through.title = "已返回记录截至：\(month.through)"
            } else {
                row.value.title = "本月：未获取（不代表零消耗）"
                row.through.title = "服务端未返回本月日记录"
            }
        }
    }

    // Recompute visible time labels from the local clock without any fetch,
    // account refresh, or root-menu reconstruction.
    func refreshDisplayedTimes(now: Date = Date()) {
        refreshTokenMonthRows(now: now)
        refreshVisibleItems(now: now)
    }

    private func refreshVisibleItems(now: Date = Date()) {
        guard self.isMenuOpen else { return }
        if let host = self.headerHost {
            let header = self.usageHeader(now: now)
            host.rootView = header
            host.frame.size.height = header.showsFailure ? UsageHeaderView.failureHeight : UsageHeaderView.baseHeight
        }
        let records = ProfileHealth.build(profiles: self.store.config.profiles,
            statuses: self.store.statuses, activeProfileId: self.store.liveProfileId,
            canActivateAuth: { self.store.authCanBeActivated(for: $0) })
        for health in records {
            guard let host = self.profileHosts[health.profile.id] else { continue }
            let duplicate = self.duplicateSummary(for: health.profile.id)
            host.rootView = ProfileCardView(profile: health.profile, status: health.status,
                isActive: health.isActive, duplicateLine: duplicate,
                onSwitch: { [weak self] in self?.selectProfile(health.profile.id) }, now: now)
            host.frame.size.height = self.cardHeight(for: health.status, hasDuplicate: duplicate != nil)
        }
        self.refreshMenuItem?.title = self.usageProvider.isRefreshing
            ? "正在刷新…（点击后再刷新一次）" : "刷新账号额度"
        self.menu?.update()
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
        self.usageProvider.cancelRefreshes()
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
                await self.usageProvider.cancelAndWait()
                try await self.beforeAccountMutation?()
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

    @objc private func openWarmupWindow() {
        self.menu?.cancelTracking()
        DispatchQueue.main.async { [weak self] in self?.openWarmup() }
    }

    func warmupProfiles() -> [WarmupProfileItem] {
        self.store.config.profiles.map { profile in
            let available = self.store.authStoreExists(for: profile.id)
            return WarmupProfileItem(id: profile.id, label: profile.label, isAvailable: available)
        }
    }

    func warmupAuthData(profileID: String) async throws -> Data {
        guard !self.store.isAuthMutationInProgress(),
              self.store.config.profiles.contains(where: { $0.id == profileID }) else {
            throw CodexRPCError.requestFailed("账号正在切换或已移除")
        }
        let source = self.store.usageAuthSource()
        let isLive = self.store.liveProfileId == profileID
        return try await Task.detached {
            guard let saved = try source.vault.loadAuthBlob(profileID: profileID) else { throw AuthError.notFound }
            if isLive, FileManager.default.fileExists(atPath: source.liveAuthURL.path) {
                let live = try Data(contentsOf: source.liveAuthURL)
                guard AuthBlob.identityMatches(saved, live) else { throw CodexRPCError.requestFailed("当前登录与所选账号不一致") }
                return live
            }
            return saved
        }.value
    }

    func performAccountMutation<T>(_ operation: @MainActor () async throws -> T) async throws -> T {
        guard !store.isAuthMutationInProgress() else { throw CodexRPCError.requestFailed("另一个账号操作尚未结束") }
        store.beginAuthMutation()
        defer { store.endAuthMutation() }
        await usageProvider.cancelAndWait()
        try await beforeAccountMutation?()
        return try await operation()
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
        Task { @MainActor in
            do {
                try await self.performAccountMutation {
                    try self.store.importLiveAuth(to: target.id)
                    self.store.updateLabel(for: target.id, label: label)
                }
                self.syncActiveProfile(force: true)
                self.requestRefresh(force: true)
                self.onChange()
            } catch { self.showError(error, "保存当前登录失败") }
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
        Task { @MainActor in
            do {
                try await self.performAccountMutation { try self.store.removeProfile(profile.id) }
                self.onChange()
            } catch { self.showError(error, "无法移除账号") }
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
        Task { @MainActor in
            do {
                try await self.performAccountMutation {
                    try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
                        CodexBridge.startLogin(profileId: id) { result in
                            continuation.resume(with: result.mapError { $0 as Error })
                        }
                    }
                }
                self.syncActiveProfile(force: true)
                self.requestRefresh(force: true)
                self.onChange()
            } catch {
                if (error as? CodexBridgeError)?.isUserCancelled != true { self.showError(error, "账号登录失败") }
            }
        }
    }

    private func requestRefresh(force: Bool = false) {
        self.usageProvider.refreshAll(force: force)
    }

    private func updateIcon() {
        guard let active = self.store.liveProfileId,
              let snapshot = self.store.statuses[active]?.snapshot else {
            self.onIconChange(IconRenderer.renderEmpty())
            return
        }
        self.onIconChange(IconRenderer.render(
            primaryRemainingPercent: RemainingQuotaPresentation.percent(
                fromUsed: snapshot.primaryUsedPercent),
            secondaryRemainingPercent: RemainingQuotaPresentation.percent(
                fromUsed: snapshot.secondaryUsedPercent)))
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
