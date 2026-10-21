import Foundation
import CodexProfileCore

enum TokenUsagePresentation {
    static func hundredMillions(_ value: Int64) -> String {
        if value > 0 && value < 500_000 { return "<0.01 亿" }
        return String(format: "%.2f 亿", Double(value) / 100_000_000)
    }
    static func millions(_ value: Int64) -> String {
        if value > 0 && value < 5_000 { return "<0.01 百万" }
        return String(format: "%.2f 百万", Double(value) / 1_000_000)
    }

    struct MonthUsage {
        let tokens: Int64
        let through: String
    }

    /// Bucket dates are service calendar dates; do not reinterpret them as UTC instants.
    static func monthUsage(_ activity: TokenActivity?, now: Date = Date()) -> MonthUsage? {
        let calendar = BeijingTime.calendar
        let parts = calendar.dateComponents([.year, .month, .day], from: now)
        let prefix = String(format: "%04d-%02d-", parts.year!, parts.month!)
        let today = prefix + String(format: "%02d", parts.day!)
        let dayCount = calendar.range(of: .day, in: .month, for: now)!.count
        var byDate: [String: Int64] = [:]
        for day in activity?.dailyUsageBuckets ?? [] {
            guard day.startDate.count == 10, day.startDate.hasPrefix(prefix), day.startDate <= today,
                  let number = Int(day.startDate.suffix(2)), (1...dayCount).contains(number), day.tokens >= 0 else { continue }
            // Repeated buckets must not be counted twice.
            byDate[day.startDate] = max(byDate[day.startDate] ?? 0, day.tokens)
        }
        guard let through = byDate.keys.max() else { return nil }
        var total: Int64 = 0
        for value in byDate.values {
            let sum = total.addingReportingOverflow(value)
            guard !sum.overflow else { return nil }
            total = sum.partialValue
        }
        return MonthUsage(tokens: total, through: through)
    }
    static func compact(_ value: Int64) -> String {
        if value >= 100_000_000 { return String(format: "%.2f 亿", Double(value) / 100_000_000) }
        if value >= 10_000 { return String(format: "%.2f 万", Double(value) / 10_000) }
        return String(value)
    }

    static func exact(_ value: Int64) -> String {
        value.formatted(.number.locale(Locale(identifier: "en_US")))
    }

    static func isCached(_ status: ProfileStatus) -> Bool {
        if case .available = status { return status.snapshot?.tokenActivityUnavailable == true }
        return true
    }

    static func cardLabel(_ status: ProfileStatus, now: Date = Date()) -> String {
        let activity = status.snapshot?.tokenActivity
        let lifetime = activity?.lifetimeTokens.map(compact) ?? "未获取"
        let month = monthUsage(activity, now: now).map { hundredMillions($0.tokens) } ?? "未获取"
        return "累计 Token：\(lifetime) · 本月：\(month)"
    }

    static func cardNote(_ status: ProfileStatus, now: Date = Date()) -> String {
        let activity = status.snapshot?.tokenActivity
        let prefix = isCached(status) && activity != nil ? "缓存 · " : ""
        if let month = monthUsage(activity, now: now) {
            return prefix + "本月已返回记录 · 截至 \(month.through)"
        }
        if let last = activity?.dailyUsageBuckets?.map(\.startDate).max() {
            return prefix + "本月未返回记录 · 最新 \(last)"
        }
        return prefix + "本月未返回记录，不代表零消耗"
    }

    struct Entry {
        let identity: String?
        let activity: TokenActivity?
        let cached: Bool
    }

    struct Summary {
        let values: String
        let coverage: String
    }

    static func summary(_ entries: [Entry], now: Date = Date()) -> Summary {
        let groups = Dictionary(grouping: entries.filter { $0.identity != nil }, by: { $0.identity! })
        let unknown = entries.filter { $0.identity == nil }.count
        var lifetime: Int64 = 0, monthly: Int64 = 0
        var lifetimeCount = 0, monthCount = 0
        var cached = false
        for group in groups.values {
            guard let newest = group.filter({ $0.activity != nil })
                .max(by: { $0.activity!.fetchedAt < $1.activity!.fetchedAt }) else { continue }
            cached = cached || newest.cached
            if let total = newest.activity?.lifetimeTokens {
                let sum = lifetime.addingReportingOverflow(total)
                guard !sum.overflow else { return Summary(values: "Token 合计：数值超出范围", coverage: "") }
                lifetime = sum.partialValue
                lifetimeCount += 1
            }
            if let month = monthUsage(newest.activity, now: now) {
                let sum = monthly.addingReportingOverflow(month.tokens)
                guard !sum.overflow else { return Summary(values: "Token 合计：数值超出范围", coverage: "") }
                monthly = sum.partialValue
                monthCount += 1
            }
        }
        let count = groups.count + unknown
        let lifetimeText = lifetimeCount > 0 ? compact(lifetime) + (lifetimeCount < count ? "（部分）" : "") : "未获取"
        let monthText = monthCount > 0 ? hundredMillions(monthly) + (monthCount < count ? "（部分）" : "") : "未获取"
        return Summary(values: "累计：\(lifetimeText) · 本月：\(monthText)",
                       coverage: "账号覆盖：累计 \(lifetimeCount)/\(count) · 本月 \(monthCount)/\(count)"
                       + (cached ? " · 含缓存" : ""))
    }

    /// Unknown identities are excluded instead of risking a duplicated total.
    static func aggregate(_ entries: [Entry]) -> String {
        let groups = Dictionary(grouping: entries.filter { $0.identity != nil }, by: { $0.identity! })
        let unknown = entries.filter { $0.identity == nil }.count
        var total: Int64 = 0
        var covered = 0
        var cached = false
        for group in groups.values {
            guard let newest = group.filter({ $0.activity?.lifetimeTokens != nil })
                .max(by: { $0.activity!.fetchedAt < $1.activity!.fetchedAt }),
                let value = newest.activity?.lifetimeTokens else { continue }
            let addition = total.addingReportingOverflow(value)
            guard !addition.overflow else { return "Token 合计：数值超出范围" }
            total = addition.partialValue
            covered += 1
            cached = cached || newest.cached
        }
        guard covered > 0 else { return "Token 合计：未获取" }
        let missing = groups.count - covered + unknown
        return "Token 合计：\(compact(total)) · \(covered) 账号"
            + (missing > 0 ? " · \(missing) 未计入" : "")
            + (cached ? " · 含缓存" : "")
    }
}
