import Foundation

/// Presentation/calendar policy only. Dates remain absolute instants from the
/// system clock; never add a timezone offset to a server or stored timestamp.
public enum BeijingTime {
    public static let identifier = "Asia/Shanghai"
    public static let timeZone = TimeZone(identifier: identifier)!

    public static var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_US_POSIX")
        calendar.timeZone = timeZone
        return calendar
    }

    public static func formatter(pattern: String) -> DateFormatter {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "zh_CN")
        formatter.calendar = calendar
        formatter.timeZone = timeZone
        formatter.dateFormat = pattern
        return formatter
    }

    public static func format(_ date: Date, pattern: String) -> String {
        formatter(pattern: pattern).string(from: date)
    }

    /// For human-readable logs/diagnostics; storage and RPC encoders keep their
    /// existing wire format. The offset is explicit and round-trips the instant.
    public static func iso8601String(_ date: Date, fractionalSeconds: Bool = false) -> String {
        let formatter = ISO8601DateFormatter()
        formatter.timeZone = timeZone
        formatter.formatOptions = [.withInternetDateTime]
        if fractionalSeconds { formatter.formatOptions.insert(.withFractionalSeconds) }
        return formatter.string(from: date)
    }
}
