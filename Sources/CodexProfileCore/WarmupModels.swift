import Foundation

/// The only timezone used by the first warmup scheduler implementation.
/// Keeping this value in the model prevents a plan from silently changing its
/// meaning when the machine's timezone changes.
public enum WarmupTimeZone {
    public static let identifier = "Asia/Shanghai"
}

public enum WarmupModelError: LocalizedError, Equatable {
    case invalidPlan(String)
    case invalidDate
    case invalidTime
    case unsupportedTimeZone(String)

    public var errorDescription: String? {
        switch self {
        case .invalidPlan(let message): return message
        case .invalidDate: return "日期无效"
        case .invalidTime: return "时间无效"
        case .unsupportedTimeZone(let value): return "不支持的时区：\(value)"
        }
    }
}

public struct WarmupLocalDate: Codable, Equatable, Hashable, Sendable {
    public let year: Int
    public let month: Int
    public let day: Int

    public init(year: Int, month: Int, day: Int) {
        self.year = year
        self.month = month
        self.day = day
    }

    public var components: DateComponents {
        DateComponents(year: year, month: month, day: day)
    }
}

public struct WarmupLocalTime: Codable, Equatable, Hashable, Sendable {
    public let hour: Int
    public let minute: Int

    public init(hour: Int, minute: Int) {
        self.hour = hour
        self.minute = minute
    }
}

public enum WarmupRecurrence: String, Codable, CaseIterable, Sendable {
    case once
    case daily
    case weekdays
}

/// Which clock the user edited. The planner stores the canonical target reset
/// time, while retaining this value so reopening the window preserves the
/// user's chosen input mode.
public enum WarmupInputMode: String, Codable, CaseIterable, Sendable {
    case trigger
    case reset
}

public struct WarmupPlan: Codable, Equatable, Sendable {
    public var id: String
    public var enabled: Bool
    public var recurrence: WarmupRecurrence
    /// Required for `.once`; ignored dates are rejected for repeating plans so
    /// an edit cannot accidentally retain a stale one-shot date.
    public var targetResetDate: WarmupLocalDate?
    public var targetResetTime: WarmupLocalTime
    public var inputMode: WarmupInputMode
    public var profileIDs: [String]
    public var revision: Int
    public let timeZoneIdentifier: String

    public init(
        id: String,
        enabled: Bool = false,
        recurrence: WarmupRecurrence,
        targetResetDate: WarmupLocalDate? = nil,
        targetResetTime: WarmupLocalTime,
        inputMode: WarmupInputMode = .reset,
        profileIDs: [String],
        revision: Int = 0,
        timeZoneIdentifier: String = WarmupTimeZone.identifier
    ) {
        self.id = id
        self.enabled = enabled
        self.recurrence = recurrence
        self.targetResetDate = targetResetDate
        self.targetResetTime = targetResetTime
        self.inputMode = inputMode
        self.profileIDs = profileIDs
        self.revision = revision
        self.timeZoneIdentifier = timeZoneIdentifier
    }

    public func validate() throws {
        guard !id.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw WarmupModelError.invalidPlan("计划 ID 不能为空")
        }
        guard profileIDs.count <= 4, !profileIDs.isEmpty else {
            throw WarmupModelError.invalidPlan("预热账号数必须为 1–4 个")
        }
        let normalized = profileIDs.map { $0.trimmingCharacters(in: .whitespacesAndNewlines) }
        guard normalized.allSatisfy({ !$0.isEmpty }), Set(normalized).count == normalized.count else {
            throw WarmupModelError.invalidPlan("预热账号 ID 必须非空且不能重复")
        }
        guard (0...23).contains(targetResetTime.hour), (0...59).contains(targetResetTime.minute) else {
            throw WarmupModelError.invalidTime
        }
        switch recurrence {
        case .once:
            guard targetResetDate != nil else {
                throw WarmupModelError.invalidPlan("单次计划必须指定目标重置日")
            }
        case .daily, .weekdays:
            guard targetResetDate == nil else {
                throw WarmupModelError.invalidPlan("重复计划不应保存单次目标重置日")
            }
        }
        guard revision >= 0 else { throw WarmupModelError.invalidPlan("计划 revision 无效") }
        guard timeZoneIdentifier == WarmupTimeZone.identifier else {
            throw WarmupModelError.unsupportedTimeZone(timeZoneIdentifier)
        }
        guard WarmupPlanner.isValidDate(targetResetDate) else {
            throw WarmupModelError.invalidDate
        }
    }
}

public struct WarmupOccurrence: Codable, Equatable, Sendable {
    public let planID: String
    public let planRevision: Int
    public let targetResetDate: WarmupLocalDate
    public let targetResetTime: WarmupLocalTime
    public let triggerDate: Date

    public init(
        planID: String,
        planRevision: Int,
        targetResetDate: WarmupLocalDate,
        targetResetTime: WarmupLocalTime,
        triggerDate: Date
    ) {
        self.planID = planID
        self.planRevision = planRevision
        self.targetResetDate = targetResetDate
        self.targetResetTime = targetResetTime
        self.triggerDate = triggerDate
    }
}

public struct WarmupExecutionKey: Codable, Equatable, Hashable, Sendable {
    public let planID: String
    public let triggerDate: Date
    public let profileID: String
    /// Stable account identity, distinct from a rotating access/refresh token.
    public let accountIdentity: String

    public init(planID: String, triggerDate: Date, profileID: String, accountIdentity: String) {
        self.planID = planID
        self.triggerDate = Date(timeIntervalSince1970: triggerDate.timeIntervalSince1970.rounded(.down))
        self.profileID = profileID
        self.accountIdentity = accountIdentity
    }

    public var storageKey: String {
        let millis = Int64((triggerDate.timeIntervalSince1970 * 1_000).rounded())
        return "\(planID)|\(millis)|\(profileID)|\(accountIdentity)"
    }

    public var watermarkKey: String {
        "\(planID)|\(accountIdentity)"
    }
}

public enum WarmupRunStatus: String, Codable, CaseIterable, Sendable {
    case planned
    case preparing
    case ready
    case sending
    case requestCompleted
    case verifying
    case confirmed
    case unknown
    case failed
    case existingWindow
    case missed
    case cancelledBeforeSend

    public var isTerminal: Bool {
        switch self {
        case .confirmed, .unknown, .failed, .existingWindow, .missed, .cancelledBeforeSend:
            return true
        case .planned, .preparing, .ready, .sending, .requestCompleted, .verifying:
            return false
        }
    }

    public func allowsTransition(to next: WarmupRunStatus) -> Bool {
        if self == next { return true }
        if next == .unknown || next == .failed { return !self.isTerminal }
        switch (self, next) {
        case (.planned, .preparing), (.planned, .cancelledBeforeSend), (.planned, .missed),
             (.preparing, .ready), (.preparing, .sending), (.preparing, .existingWindow),
             (.preparing, .cancelledBeforeSend),
             (.ready, .sending), (.ready, .existingWindow), (.ready, .cancelledBeforeSend),
             (.sending, .requestCompleted),
             (.requestCompleted, .verifying),
             (.verifying, .confirmed):
            return true
        default:
            return false
        }
    }
}

public struct WarmupRunState: Codable, Equatable, Sendable {
    public let key: WarmupExecutionKey
    public let planRevision: Int
    public let identityVersion: String
    public var status: WarmupRunStatus
    public let createdAt: Date
    public var updatedAt: Date
    public var reason: String?
    public var resetAt: Date?
    public var baseline: WarmupQuotaObservation?
    public var samples: [WarmupQuotaObservation]?

    public init(
        key: WarmupExecutionKey,
        planRevision: Int,
        identityVersion: String,
        status: WarmupRunStatus = .sending,
        createdAt: Date,
        updatedAt: Date,
        reason: String? = nil
    ) {
        self.key = key
        self.planRevision = planRevision
        self.identityVersion = identityVersion
        self.status = status
        self.createdAt = createdAt
        self.updatedAt = updatedAt
        self.reason = reason
    }
}

public struct WarmupControl: Codable, Equatable, Sendable {
    public static let currentSchemaVersion = 1

    public var schemaVersion: Int
    public var plan: WarmupPlan?
    public var planRevision: Int
    /// Latest terminal trigger per plan/profile/identity. This survives result
    /// cleanup and prevents an old slot from being sent again after a restart.
    public var executionWatermarks: [String: Date]
    /// Active runs plus a bounded set of recent terminal summaries. Detailed
    /// evidence lives outside this control file.
    public var runs: [WarmupRunState]

    public init(
        schemaVersion: Int = WarmupControl.currentSchemaVersion,
        plan: WarmupPlan? = nil,
        planRevision: Int = 0,
        executionWatermarks: [String: Date] = [:],
        runs: [WarmupRunState] = []
    ) {
        self.schemaVersion = schemaVersion
        self.plan = plan
        self.planRevision = planRevision
        self.executionWatermarks = executionWatermarks
        self.runs = runs
    }
}
