import Foundation
import CodexProfileCore

/// Pure presentation of a server-reported quota reset instant.
///
/// The input `Date` remains an absolute instant. BeijingTime is used only for
/// the human-facing calendar and clock text, so the display cannot drift with
/// the machine's local timezone or daylight-saving rules.
enum QuotaResetPresentation {
    enum State: Equatable {
        case future
        case notReturned
        case awaitingRefresh
    }

    struct Value: Equatable {
        let text: String
        let compactText: String
        let help: String
        let accessibilityLabel: String
        let state: State
    }

    static func make(resetAt: Date?, now: Date, isCached: Bool) -> Value {
        guard let resetAt else {
            let missing = isCached ? "缓存·未返回重置时刻" : "重置时间未返回"
            let cacheNote = isCached ? "额度来自缓存；" : ""
            let help = "\(cacheNote)服务端未返回重置时刻（北京时间）；等待下一次额度刷新"
            return Value(text: missing, compactText: missing, help: help,
                         accessibilityLabel: help, state: .notReturned)
        }

        let fullDate = BeijingTime.format(resetAt, pattern: "yyyy年M月d日 HH:mm:ss")
        let dateLabel = self.inlineDate(resetAt, relativeTo: now)
        let fullReport = "服务器报告重置：\(fullDate)（北京时间）"

        guard resetAt > now else {
            let text = isCached ? "缓存\(dateLabel) · 待刷新" : "\(dateLabel) 已到 · 待刷新"
            let compact = isCached ? "缓存\(dateLabel)·待刷新" : "\(dateLabel)·待刷新"
            let cacheNote = isCached ? "额度来自缓存；" : ""
            let help = "\(fullReport)；\(cacheNote)等待新读数，不代表已重置或预热成功"
            return Value(text: text, compactText: compact, help: help,
                         accessibilityLabel: help, state: .awaitingRefresh)
        }

        let remaining = self.remainingText(from: now, to: resetAt)
        let text: String
        let compact: String
        let remainingSuffix = remaining == "不足1分" ? remaining : "余\(remaining)"
        if isCached {
            text = "缓存\(dateLabel) · \(remainingSuffix)"
            compact = "缓存\(dateLabel)·\(remainingSuffix)"
        } else {
            text = "\(dateLabel) 重置 · \(remainingSuffix)"
            compact = "\(dateLabel)·\(remaining)"
        }
        let cacheNote = isCached ? "；额度来自缓存" : ""
        let help = "\(fullReport)；还剩\(self.fullRemainingText(from: now, to: resetAt))\(cacheNote)"
        return Value(text: text, compactText: compact, help: help,
                     accessibilityLabel: help, state: .future)
    }

    static func isCachedQuota(_ status: ProfileStatus) -> Bool {
        switch status {
        case .stale(.some), .reloginNeeded(.some):
            return true
        case .available, .stale(nil), .reloginNeeded(nil), .loading, .notSetUp:
            return false
        }
    }

    private static func inlineDate(_ resetAt: Date, relativeTo now: Date) -> String {
        let calendar = BeijingTime.calendar
        let resetComponents = calendar.dateComponents([.year, .month, .day], from: resetAt)
        let nowComponents = calendar.dateComponents([.year, .month, .day], from: now)
        if resetComponents == nowComponents {
            return BeijingTime.format(resetAt, pattern: "HH:mm")
        }
        if let tomorrow = calendar.date(byAdding: .day, value: 1, to: now),
           calendar.dateComponents([.year, .month, .day], from: tomorrow) == resetComponents {
            return "明日" + BeijingTime.format(resetAt, pattern: "HH:mm")
        }
        return BeijingTime.format(resetAt, pattern: "M/d HH:mm")
    }

    private static func remainingText(from now: Date, to resetAt: Date) -> String {
        let seconds = max(0, Int(resetAt.timeIntervalSince(now)))
        guard seconds >= 60 else { return "不足1分" }
        let days = seconds / 86_400
        let hours = (seconds % 86_400) / 3_600
        let minutes = (seconds % 3_600) / 60
        if days > 0 { return "\(days)天\(hours)时" }
        if hours > 0 { return "\(hours)时\(minutes)分" }
        return "\(minutes)分"
    }

    private static func fullRemainingText(from now: Date, to resetAt: Date) -> String {
        let seconds = max(0, Int(resetAt.timeIntervalSince(now)))
        guard seconds > 0 else { return "不足1秒" }
        let days = seconds / 86_400
        let hours = (seconds % 86_400) / 3_600
        let minutes = (seconds % 3_600) / 60
        let remainder = seconds % 60
        if days > 0 { return "\(days)天\(hours)小时\(minutes)分\(remainder)秒" }
        if hours > 0 { return "\(hours)小时\(minutes)分\(remainder)秒" }
        if minutes > 0 { return "\(minutes)分\(remainder)秒" }
        return "\(remainder)秒"
    }
}
