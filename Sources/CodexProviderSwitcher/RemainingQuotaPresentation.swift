enum RemainingQuotaUrgency: Equatable {
    case normal
    case warning
    case critical
}

enum RemainingQuotaPresentation {
    static func percent(fromUsed usedPercent: Int) -> Int {
        min(100, max(0, 100 - usedPercent))
    }

    static func urgency(forRemaining remainingPercent: Int) -> RemainingQuotaUrgency {
        let percent = min(100, max(0, remainingPercent))
        if percent <= 10 { return .critical }
        if percent <= 30 { return .warning }
        return .normal
    }
}
