import Foundation

public enum WarmupPlanner {
    public static var calendar: Calendar {
        var calendar = Calendar(identifier: .gregorian)
        calendar.locale = Locale(identifier: "en_US_POSIX")
        calendar.timeZone = TimeZone(identifier: WarmupTimeZone.identifier)!
        return calendar
    }

    public static func isValidDate(_ date: WarmupLocalDate?) -> Bool {
        guard let date else { return true }
        guard (1...12).contains(date.month), (1...31).contains(date.day) else { return false }
        var components = date.components
        components.calendar = calendar
        guard let value = calendar.date(from: components) else { return false }
        let actual = calendar.dateComponents([.year, .month, .day], from: value)
        return actual.year == date.year && actual.month == date.month && actual.day == date.day
    }

    public static func date(
        _ date: WarmupLocalDate,
        time: WarmupLocalTime
    ) throws -> Date {
        guard isValidDate(date) else { throw WarmupModelError.invalidDate }
        guard (0...23).contains(time.hour), (0...59).contains(time.minute) else {
            throw WarmupModelError.invalidTime
        }
        var components = date.components
        components.hour = time.hour
        components.minute = time.minute
        components.second = 0
        components.nanosecond = 0
        components.calendar = calendar
        guard let value = calendar.date(from: components) else { throw WarmupModelError.invalidDate }
        let actual = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: value)
        guard actual.year == date.year, actual.month == date.month, actual.day == date.day,
              actual.hour == time.hour, actual.minute == time.minute else {
            throw WarmupModelError.invalidDate
        }
        return value
    }

    /// The reset target is five hours after the trigger. Subtracting via the
    /// fixed timezone calendar correctly crosses midnight and makes the rule
    /// explicit instead of relying on a local `Date` formatter.
    public static func triggerDate(
        targetResetDate: WarmupLocalDate,
        targetResetTime: WarmupLocalTime
    ) throws -> Date {
        let target = try date(targetResetDate, time: targetResetTime)
        guard let trigger = calendar.date(byAdding: .hour, value: -5, to: target) else {
            throw WarmupModelError.invalidDate
        }
        return trigger
    }

    public static func occurrence(
        for targetDate: WarmupLocalDate,
        plan: WarmupPlan
    ) throws -> WarmupOccurrence {
        try plan.validate()
        guard plan.recurrence != .once || plan.targetResetDate == targetDate else {
            throw WarmupModelError.invalidPlan("目标重置日与单次计划不一致")
        }
        guard let trigger = try? triggerDate(
            targetResetDate: targetDate,
            targetResetTime: plan.targetResetTime) else {
            throw WarmupModelError.invalidDate
        }
        return WarmupOccurrence(
            planID: plan.id,
            planRevision: plan.revision,
            targetResetDate: targetDate,
            targetResetTime: plan.targetResetTime,
            triggerDate: trigger)
    }

    /// Returns the first trigger at or after `now`. Repeating-plan eligibility
    /// is evaluated on the target reset date, not on the prior-day trigger date.
    public static func nextOccurrence(
        after now: Date,
        plan: WarmupPlan
    ) throws -> WarmupOccurrence? {
        try plan.validate()
        guard plan.enabled else { return nil }

        if plan.recurrence == .once {
            guard let targetDate = plan.targetResetDate else { return nil }
            let result = try occurrence(for: targetDate, plan: plan)
            return result.triggerDate >= now ? result : nil
        }

        let localNow = calendar.dateComponents([.year, .month, .day], from: now)
        guard let start = calendar.date(from: DateComponents(
            year: localNow.year, month: localNow.month, day: localNow.day)) else {
            throw WarmupModelError.invalidDate
        }
        // A daily trigger can fall on the preceding local date (for a target
        // before 05:00), so searching target dates forward from today is both
        // sufficient and keeps weekday semantics tied to the reset date.
        for offset in 0...370 {
            guard let candidate = calendar.date(byAdding: .day, value: offset, to: start) else { continue }
            let components = calendar.dateComponents([.year, .month, .day, .weekday], from: candidate)
            guard let year = components.year, let month = components.month, let day = components.day else { continue }
            if plan.recurrence == .weekdays,
               components.weekday == 1 || components.weekday == 7 {
                continue
            }
            let targetDate = WarmupLocalDate(year: year, month: month, day: day)
            let result = try occurrence(for: targetDate, plan: plan)
            if result.triggerDate >= now { return result }
        }
        return nil
    }

    public static func localComponents(for date: Date) -> (date: WarmupLocalDate, time: WarmupLocalTime) {
        let components = calendar.dateComponents([.year, .month, .day, .hour, .minute], from: date)
        return (
            WarmupLocalDate(year: components.year!, month: components.month!, day: components.day!),
            WarmupLocalTime(hour: components.hour!, minute: components.minute!))
    }
}
