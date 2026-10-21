import Combine
import CodexProfileCore
import Foundation

enum WarmupTimeMode: String, CaseIterable, Identifiable {
    case warmup, reset
    var id: String { rawValue }
    var title: String { self == .warmup ? "预热时刻" : "目标重置时刻" }
}

enum WarmupRepeat: String, CaseIterable, Identifiable {
    case daily, weekdays, once
    var id: String { rawValue }
    var title: String {
        switch self {
        case .daily: return "每天"
        case .weekdays: return "周一至周五"
        case .once: return "仅一次"
        }
    }
}

struct WarmupDraftPlan: Equatable {
    var enabled = false
    var accounts: Set<String> = []
    var mode: WarmupTimeMode = .warmup
    var time = "04:00"
    var repeatRule: WarmupRepeat = .daily
    var targetDate: String
}

struct WarmupProfileItem: Identifiable, Equatable {
    let id: String
    let label: String
    let isAvailable: Bool
}

struct WarmupResultRow: Identifiable, Equatable {
    let id: String
    let label: String
    let statusTitle: String
    let resetAt: Date?
    let detail: String
}

@MainActor
final class WarmupViewModel: ObservableObject {
    @Published private(set) var currentTime: Date
    @Published var resources: WarmupResourceSnapshot?
    @Published var draft: WarmupDraftPlan
    @Published var savedPlan: CodexProfileCore.WarmupPlan?
    @Published var profiles: [WarmupProfileItem] = [] {
        didSet {
            if !didChooseDefaults, savedPlan == nil, draft.accounts.isEmpty, !profiles.isEmpty {
                draft.accounts = Set(profiles.filter(\.isAvailable).prefix(4).map(\.id))
                didChooseDefaults = true
            }
        }
    }
    @Published var results: [WarmupResultRow] = []
    @Published var isRunning = false
    @Published var nextExecutionAt: Date?
    @Published var lastMessage = ""
    @Published var maintenanceSummary = ""
    @Published private(set) var controlLoadError: String?

    var onSave: ((CodexProfileCore.WarmupPlan) throws -> CodexProfileCore.WarmupPlan)?
    var onRun: (([String]) -> Void)?
    var onRecheck: (() -> Void)?
    private let now: () -> Date
    private var didChooseDefaults = false

    init(now: @escaping () -> Date = Date.init) {
        self.now = now
        self.currentTime = now()
        let tomorrow = WarmupPlanner.calendar.date(byAdding: .day, value: 1, to: now())!
        let local = WarmupPlanner.localComponents(for: tomorrow).date
        draft = WarmupDraftPlan(targetDate: Self.dateString(local))
    }

    func loadSavedPlan(_ plan: CodexProfileCore.WarmupPlan?) {
        controlLoadError = nil
        savedPlan = plan
        guard let plan else { return }
        didChooseDefaults = true
        let minutes = plan.targetResetTime.hour * 60 + plan.targetResetTime.minute
        draft = WarmupDraftPlan(
            enabled: plan.enabled,
            accounts: Set(plan.profileIDs),
            mode: plan.inputMode == .trigger ? .warmup : .reset,
            time: Self.clock(minutes - (plan.inputMode == .trigger ? 300 : 0)),
            repeatRule: WarmupRepeat(rawValue: plan.recurrence.rawValue) ?? .daily,
            targetDate: plan.targetResetDate.map(Self.dateString) ?? draft.targetDate)
        lastMessage = "已读取已保存计划"
    }

    func setControlLoadError(_ message: String) {
        controlLoadError = message
        lastMessage = "预热控制状态读取失败：\(message)"
    }

    var hasUnsavedChanges: Bool {
        guard let savedPlan, let current = try? makePlan(requireFuture: false) else { return true }
        return current != savedPlan
    }

    var validationMessage: String? {
        do { _ = try makePlan(); return nil }
        catch { return error.localizedDescription }
    }

    var canRun: Bool { !isRunning && controlLoadError == nil && (try? selectedProfileIDs()) != nil && onRun != nil }

    func makePlan(requireFuture: Bool = true) throws -> CodexProfileCore.WarmupPlan {
        if let controlLoadError { throw WarmupModelError.invalidPlan("控制状态不可用：\(controlLoadError)") }
        let ids = try selectedProfileIDs(requireAvailable: draft.enabled || savedPlan == nil)
        let parts = draft.time.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 2, parts[0].count == 2, parts[1].count == 2,
              let hour = Int(parts[0]), let minute = Int(parts[1]), (0...23).contains(hour), (0...59).contains(minute) else {
            throw WarmupModelError.invalidTime
        }
        let normalized = (hour * 60 + minute + (draft.mode == .warmup ? 300 : 0)) % 1440
        let targetDate: WarmupLocalDate?
        if draft.repeatRule == .once {
            let parts = draft.targetDate.split(separator: "-").compactMap { Int($0) }
            guard parts.count == 3 else { throw WarmupModelError.invalidDate }
            targetDate = .init(year: parts[0], month: parts[1], day: parts[2])
        } else { targetDate = nil }
        let plan = CodexProfileCore.WarmupPlan(
            id: savedPlan?.id ?? "default",
            enabled: draft.enabled,
            recurrence: WarmupRecurrence(rawValue: draft.repeatRule.rawValue) ?? .daily,
            targetResetDate: targetDate,
            targetResetTime: .init(hour: normalized / 60, minute: normalized % 60),
            inputMode: draft.mode == .warmup ? .trigger : .reset,
            profileIDs: ids,
            revision: savedPlan?.revision ?? 0)
        try plan.validate()
        if requireFuture, plan.enabled, let targetDate {
            let trigger = try WarmupPlanner.triggerDate(targetResetDate: targetDate, targetResetTime: plan.targetResetTime)
            guard trigger > now() else { throw WarmupModelError.invalidPlan("单次计划的预热时刻必须晚于当前时间") }
        }
        return plan
    }

    func setMode(_ mode: WarmupTimeMode) {
        guard mode != draft.mode else { return }
        let parts = draft.time.split(separator: ":").compactMap { Int($0) }
        guard parts.count == 2, (0...23).contains(parts[0]), (0...59).contains(parts[1]) else { return }
        draft.time = Self.clock(parts[0] * 60 + parts[1] + (mode == .reset ? 300 : -300))
        draft.mode = mode
    }

    func saveDraft() {
        guard !isRunning else { return }
        do {
            let plan = try makePlan()
            guard let onSave else { throw WarmupModelError.invalidPlan("保存服务尚未就绪") }
            let saved = try onSave(plan)
            loadSavedPlan(saved)
            lastMessage = "计划已保存"
        } catch { lastMessage = "保存失败：\(error.localizedDescription)" }
    }

    func runNow() {
        guard !isRunning else { return }
        do {
            if let controlLoadError { throw WarmupModelError.invalidPlan(controlLoadError) }
            let ids = try selectedProfileIDs()
            guard let onRun else { throw WarmupModelError.invalidPlan("预热服务尚未就绪") }
            onRun(ids)
        } catch { lastMessage = "无法预热：\(error.localizedDescription)" }
    }

    func recheck() {
        guard !isRunning, controlLoadError == nil else { return }
        onRecheck?()
    }

    func setSelected(_ id: String, selected: Bool) {
        if selected {
            guard draft.accounts.count < 4, profiles.contains(where: { $0.id == id && $0.isAvailable }) else { return }
            draft.accounts.insert(id)
        } else { draft.accounts.remove(id) }
    }

    var preview: String {
        guard var plan = try? makePlan(requireFuture: false) else { return "填写有效时间并选择 1–4 个可用账号" }
        plan.enabled = true
        let occurrence: WarmupOccurrence?
        if let date = plan.targetResetDate { occurrence = try? WarmupPlanner.occurrence(for: date, plan: plan) }
        else { occurrence = try? WarmupPlanner.nextOccurrence(after: now(), plan: plan) }
        guard let occurrence, let reset = try? WarmupPlanner.date(occurrence.targetResetDate, time: occurrence.targetResetTime) else { return "没有可执行的计划时点" }
        return "\(Self.display(occurrence.triggerDate)) 预热 → 预计 \(Self.display(reset)) 重置"
    }

    var timePickerDate: Date {
        get {
            let p = draft.time.split(separator: ":").compactMap { Int($0) }
            return (try? WarmupPlanner.date(.init(year: 2001, month: 1, day: 1), time: .init(hour: p.first ?? 4, minute: p.dropFirst().first ?? 0))) ?? now()
        }
        set {
            let time = WarmupPlanner.localComponents(for: newValue).time
            draft.time = String(format: "%02d:%02d", time.hour, time.minute)
        }
    }

    var targetPickerDate: Date {
        get {
            let p = draft.targetDate.split(separator: "-").compactMap { Int($0) }
            guard p.count == 3 else { return now() }
            return (try? WarmupPlanner.date(.init(year: p[0], month: p[1], day: p[2]), time: .init(hour: 12, minute: 0))) ?? now()
        }
        set { draft.targetDate = Self.dateString(WarmupPlanner.localComponents(for: newValue).date) }
    }

    func refreshClock() { currentTime = now() }

    var currentTimeText: String { Self.display(currentTime, includesSeconds: true) }

    static func display(_ date: Date, includesSeconds: Bool = false) -> String {
        BeijingTime.format(date, pattern: includesSeconds ? "yyyy年M月d日 HH:mm:ss" : "yyyy年M月d日 HH:mm")
    }

    private func selectedProfileIDs(requireAvailable: Bool = true) throws -> [String] {
        guard (1...4).contains(draft.accounts.count) else { throw WarmupModelError.invalidPlan("请选择 1–4 个账号") }
        let available = Set(profiles.filter(\.isAvailable).map(\.id))
        guard !requireAvailable || draft.accounts.isSubset(of: available) else { throw WarmupModelError.invalidPlan("所选账号已移除或需要重新登录，请更新选择") }
        return draft.accounts.sorted()
    }

    private static func dateString(_ date: WarmupLocalDate) -> String {
        String(format: "%04d-%02d-%02d", date.year, date.month, date.day)
    }

    private static func clock(_ minutes: Int) -> String {
        let n = (minutes % 1440 + 1440) % 1440
        return String(format: "%02d:%02d", n / 60, n % 60)
    }
}
