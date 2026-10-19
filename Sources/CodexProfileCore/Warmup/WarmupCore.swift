import Foundation

// MARK: - Bounded JSON-RPC protocol primitives

/// A deliberately small JSON value used by the warm-up protocol layer.  The
/// layer keeps the decoded value bounded and does not retain the original
/// stdout line, which avoids putting prompts, responses, or credentials into
/// the warm-up state machine.
public enum WarmupJSONValue: Codable, Equatable, Sendable {
    case null
    case bool(Bool)
    case number(Double)
    case string(String)
    case array([WarmupJSONValue])
    case object([String: WarmupJSONValue])

    public init(from decoder: Decoder) throws {
        let container = try decoder.singleValueContainer()
        if container.decodeNil() {
            self = .null
        } else if let value = try? container.decode(Bool.self) {
            self = .bool(value)
        } else if let value = try? container.decode(Double.self) {
            self = .number(value)
        } else if let value = try? container.decode(String.self) {
            self = .string(value)
        } else if let value = try? container.decode([WarmupJSONValue].self) {
            self = .array(value)
        } else if let value = try? container.decode([String: WarmupJSONValue].self) {
            self = .object(value)
        } else {
            throw DecodingError.dataCorruptedError(
                in: container,
                debugDescription: "Unsupported JSON value")
        }
    }

    public func encode(to encoder: Encoder) throws {
        var container = encoder.singleValueContainer()
        switch self {
        case .null:
            try container.encodeNil()
        case .bool(let value):
            try container.encode(value)
        case .number(let value):
            try container.encode(value)
        case .string(let value):
            try container.encode(value)
        case .array(let value):
            try container.encode(value)
        case .object(let value):
            try container.encode(value)
        }
    }

    public subscript(key: String) -> WarmupJSONValue? {
        guard case .object(let object) = self else { return nil }
        return object[key]
    }
}

public struct WarmupRPCError: Codable, Equatable, Sendable {
    public let code: Int?
    public let message: String?
    public let data: WarmupJSONValue?

    public init(code: Int? = nil, message: String? = nil, data: WarmupJSONValue? = nil) {
        self.code = code
        self.message = message
        self.data = data
    }
}

/// One JSON-RPC line or server notification.  Unknown fields are ignored, so
/// a Codex CLI upgrade cannot make the dispatcher retain an unbounded payload.
public struct WarmupRPCEnvelope: Codable, Equatable, Sendable {
    public let id: Int?
    public let method: String?
    public let eventType: String?
    public let result: WarmupJSONValue?
    public let error: WarmupRPCError?

    enum CodingKeys: String, CodingKey {
        case id, method, result, error
        case eventType = "type"
    }

    public init(
        id: Int? = nil,
        method: String? = nil,
        eventType: String? = nil,
        result: WarmupJSONValue? = nil,
        error: WarmupRPCError? = nil
    ) {
        self.id = id
        self.method = method
        self.eventType = eventType
        self.result = result
        self.error = error
    }

    public var isCompletedResponse: Bool {
        self.eventType == "response.completed"
            || self.result?["status"] == .string("completed")
    }

    public var isFailedResponse: Bool {
        guard let type = self.eventType else { return self.error != nil }
        return type == "response.failed" || type == "response.incomplete" || type == "error"
    }
}

public struct WarmupOutputLimits: Equatable, Sendable {
    public let maxLineBytes: Int
    public let maxBufferedBytes: Int
    public let maxMessages: Int

    public init(
        maxLineBytes: Int = 256 * 1024,
        maxBufferedBytes: Int = 4 * 1024 * 1024,
        maxMessages: Int = 2_048
    ) {
        self.maxLineBytes = max(1, maxLineBytes)
        self.maxBufferedBytes = max(self.maxLineBytes, maxBufferedBytes)
        self.maxMessages = max(1, maxMessages)
    }
}

public enum WarmupRPCDispatchError: Error, Equatable, Sendable {
    case lineTooLarge(Int)
    case bufferTooLarge(Int)
    case messageLimitExceeded(Int)
    case malformedLine
    case incompleteLine
}

/// Thread-safe bounded line decoder for a Codex app-server stdout stream.
/// Callers must treat an error as terminal and terminate the child process;
/// continuing after an overflow could otherwise make a later completion event
/// look like a successful warm-up.
public final class WarmupRPCMessageDispatcher: @unchecked Sendable {
    private let limits: WarmupOutputLimits
    private let lock = NSLock()
    private var buffer = Data()
    private var messageCount = 0

    public init(limits: WarmupOutputLimits = WarmupOutputLimits()) {
        self.limits = limits
    }

    public func append(_ data: Data) throws -> [WarmupRPCEnvelope] {
        self.lock.lock()
        defer { self.lock.unlock() }

        self.buffer.append(data)
        guard self.buffer.count <= self.limits.maxBufferedBytes else {
            throw WarmupRPCDispatchError.bufferTooLarge(self.buffer.count)
        }

        var envelopes: [WarmupRPCEnvelope] = []
        while let newline = self.buffer.firstIndex(of: 0x0A) {
            let line = Data(self.buffer[..<newline])
            self.buffer.removeSubrange(...newline)
            if line.isEmpty { continue }
            guard line.count <= self.limits.maxLineBytes else {
                throw WarmupRPCDispatchError.lineTooLarge(line.count)
            }
            guard self.messageCount < self.limits.maxMessages else {
                throw WarmupRPCDispatchError.messageLimitExceeded(self.messageCount + 1)
            }
            guard let envelope = try? JSONDecoder().decode(WarmupRPCEnvelope.self, from: line) else {
                throw WarmupRPCDispatchError.malformedLine
            }
            self.messageCount += 1
            envelopes.append(envelope)
        }
        return envelopes
    }

    public func finish() throws -> [WarmupRPCEnvelope] {
        self.lock.lock()
        defer { self.lock.unlock() }
        guard self.buffer.isEmpty else { throw WarmupRPCDispatchError.incompleteLine }
        return []
    }
}

// MARK: - Raw multi-pool quota and confirmation

public struct WarmupQuotaWindow: Codable, Equatable, Sendable {
    public let poolID: String?
    public let usedPercent: Double?
    public let windowDurationMinutes: Int?
    public let resetsAt: Date?

    public init(
        poolID: String? = nil,
        usedPercent: Double? = nil,
        windowDurationMinutes: Int? = nil,
        resetsAt: Date? = nil
    ) {
        self.poolID = poolID
        self.usedPercent = usedPercent
        self.windowDurationMinutes = windowDurationMinutes
        self.resetsAt = resetsAt
    }
}

public struct WarmupQuotaObservation: Codable, Equatable, Sendable {
    public let accountIdentity: String?
    public let observedAt: Date
    public let windows: [WarmupQuotaWindow]

    public init(accountIdentity: String?, observedAt: Date, windows: [WarmupQuotaWindow]) {
        self.accountIdentity = accountIdentity
        self.observedAt = observedAt
        self.windows = windows
    }
}

public struct WarmupConfirmationPolicy: Equatable, Sendable {
    public let targetWindowDurationMinutes: Int
    public let minimumSamples: Int
    public let minimumSpan: TimeInterval
    public let resetJitter: TimeInterval

    public init(
        targetWindowDurationMinutes: Int = 300,
        minimumSamples: Int = 3,
        minimumSpan: TimeInterval = 60,
        resetJitter: TimeInterval = 2
    ) {
        self.targetWindowDurationMinutes = targetWindowDurationMinutes
        self.minimumSamples = max(3, minimumSamples)
        self.minimumSpan = max(0, minimumSpan)
        self.resetJitter = max(0, resetJitter)
    }
}

public enum WarmupConfirmationFailure: Equatable, Sendable {
    case baselinePoolMissing
    case poolIdentityMissing
    case accountIdentityMissing
    case accountIdentityChanged
    case poolIdentityChanged
    case insufficientSamples(actual: Int, required: Int)
    case insufficientSpan(actual: TimeInterval, required: TimeInterval)
    case resetTimeMissing
    case resetStillMoving
}

public enum WarmupConfirmationResult: Equatable, Sendable {
    case confirmed(poolID: String?, resetAt: Date)
    case unconfirmed(WarmupConfirmationFailure)
}

public enum WarmupDispatchDecision: Equatable, Sendable {
    case send
    case skipExistingWindow(resetAt: Date)
    case refuse(WarmupConfirmationFailure)
}

public enum WarmupQuotaConfirmation {
    /// Decides whether a request may be sent from one fresh baseline.  An
    /// already-active 5-hour window is never re-warmed; callers must wait for
    /// the server reset and then obtain a new baseline.  Missing identity/pool
    /// evidence is a refusal, not permission to guess.
    public static func dispatchDecision(
        baseline: WarmupQuotaObservation,
        policy: WarmupConfirmationPolicy = WarmupConfirmationPolicy()
    ) -> WarmupDispatchDecision {
        guard let account = baseline.accountIdentity, !account.isEmpty else {
            return .refuse(.accountIdentityMissing)
        }
        guard let target = baseline.windows.first(where: {
            $0.poolID == "codex" && $0.windowDurationMinutes == policy.targetWindowDurationMinutes
        }) else {
            return .refuse(.baselinePoolMissing)
        }
        guard let poolID = target.poolID, !poolID.isEmpty else {
            return .refuse(.poolIdentityMissing)
        }
        guard let resetAt = target.resetsAt else {
            return .refuse(.resetTimeMissing)
        }
        if resetAt > baseline.observedAt {
            return .skipExistingWindow(resetAt: resetAt)
        }
        return .send
    }

    /// A future reset is eligible only after two fresh observations prove the
    /// zero-use main codex pool is a moving now+5h placeholder. A single
    /// observation never establishes this distinction.
    public static func dispatchDecision(
        baselines: [WarmupQuotaObservation],
        policy: WarmupConfirmationPolicy = WarmupConfirmationPolicy()
    ) -> WarmupDispatchDecision {
        guard baselines.count >= 2 else {
            return .refuse(.insufficientSamples(actual: baselines.count, required: 2))
        }
        let ordered = baselines.sorted { $0.observedAt < $1.observedAt }
        guard let first = ordered.first, let last = ordered.last,
              let identity = first.accountIdentity, !identity.isEmpty else {
            return .refuse(.accountIdentityMissing)
        }
        guard ordered.allSatisfy({ $0.accountIdentity == identity }) else {
            return .refuse(.accountIdentityChanged)
        }
        let duration = TimeInterval(policy.targetWindowDurationMinutes * 60)
        let elapsed = last.observedAt.timeIntervalSince(first.observedAt)
        var windows: [WarmupQuotaWindow] = []
        for observation in ordered {
            let targets = observation.windows.filter {
                $0.poolID == "codex" && $0.windowDurationMinutes == policy.targetWindowDurationMinutes
            }
            guard targets.count == 1, let window = targets.first else {
                return .refuse(.baselinePoolMissing)
            }
            guard window.resetsAt != nil else { return .refuse(.resetTimeMissing) }
            windows.append(window)
        }
        if let reset = windows.last?.resetsAt, reset <= last.observedAt { return .send }
        guard elapsed > policy.resetJitter * 2 else {
            return .refuse(.insufficientSpan(actual: elapsed, required: policy.resetJitter * 2))
        }
        let isPlaceholder = zip(ordered, windows).allSatisfy { observation, window in
            window.usedPercent == 0 && window.resetsAt.map {
                abs($0.timeIntervalSince(observation.observedAt) - duration) <= policy.resetJitter
            } == true
        }
        if isPlaceholder,
           let firstReset = windows.first?.resetsAt, let lastReset = windows.last?.resetsAt,
           abs(lastReset.timeIntervalSince(firstReset) - elapsed) <= policy.resetJitter {
            return .send
        }
        if let reset = windows.last?.resetsAt {
            return .skipExistingWindow(resetAt: reset)
        }
        return .refuse(.resetTimeMissing)
    }

    /// Confirms only a stable reset for the same account and same 5-hour pool.
    /// A 0% usage value is not treated as failure; a moving reset is treated as
    /// insufficient evidence because it can be an unstarted/placeholder window.
    public static func evaluate(
        baseline: WarmupQuotaObservation,
        samples: [WarmupQuotaObservation],
        policy: WarmupConfirmationPolicy = WarmupConfirmationPolicy()
    ) -> WarmupConfirmationResult {
        guard let account = baseline.accountIdentity, !account.isEmpty else {
            return .unconfirmed(.accountIdentityMissing)
        }
        guard let target = baseline.windows.first(where: {
            $0.poolID == "codex" && $0.windowDurationMinutes == policy.targetWindowDurationMinutes
        }) else {
            return .unconfirmed(.baselinePoolMissing)
        }
        guard let targetPoolID = target.poolID, !targetPoolID.isEmpty else {
            return .unconfirmed(.poolIdentityMissing)
        }
        guard samples.count >= policy.minimumSamples else {
            return .unconfirmed(.insufficientSamples(actual: samples.count, required: policy.minimumSamples))
        }
        let ordered = samples.sorted { $0.observedAt < $1.observedAt }
        guard let first = ordered.first, let last = ordered.last else {
            return .unconfirmed(.insufficientSamples(actual: 0, required: policy.minimumSamples))
        }
        let span = last.observedAt.timeIntervalSince(first.observedAt)
        guard span >= policy.minimumSpan else {
            return .unconfirmed(.insufficientSpan(actual: span, required: policy.minimumSpan))
        }

        var resetTimes: [Date] = []
        for sample in ordered {
            guard sample.accountIdentity == baseline.accountIdentity else {
                return .unconfirmed(.accountIdentityChanged)
            }
            guard let observed = sample.windows.first(where: { $0.poolID == targetPoolID
                && $0.windowDurationMinutes == target.windowDurationMinutes
            }) else {
                return .unconfirmed(.poolIdentityChanged)
            }
            guard let resetAt = observed.resetsAt else {
                return .unconfirmed(.resetTimeMissing)
            }
            resetTimes.append(resetAt)
        }
        guard let firstReset = resetTimes.first else {
            return .unconfirmed(.resetTimeMissing)
        }
        let maxDrift = resetTimes.map { abs($0.timeIntervalSince(firstReset)) }.max() ?? 0
        guard maxDrift <= policy.resetJitter else {
            return .unconfirmed(.resetStillMoving)
        }
        return .confirmed(poolID: targetPoolID, resetAt: firstReset)
    }
}

// MARK: - Minimal CLI configuration and owned temporary homes

public struct WarmupMinimalConfiguration: Equatable, Sendable {
    public let model: String

    public init(model: String) throws {
        let trimmed = model.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty,
              trimmed.count <= 128,
              !trimmed.contains(where: { $0 == "\n" || $0 == "\r" || $0 == "\"" }) else {
            throw WarmupConfigurationError.invalidModel
        }
        self.model = trimmed
    }

    /// Deliberately constructed from an allow-list rather than copying the
    /// user's config, so provider keys, MCP entries, and project settings do
    /// not enter the isolated warm-up home.
    public var toml: String {
        "model = \"\(self.model)\"\nmodel_provider = \"openai\"\n"
    }
}

public enum WarmupConfigurationError: Error, Equatable, Sendable {
    case invalidModel
}

public struct WarmupProcessOwner: Codable, Equatable, Sendable {
    public let runID: String
    public let leaseToken: String
    public let processIdentifier: Int32
    public let startedAt: Date

    public init(
        runID: String,
        processIdentifier: Int32,
        startedAt: Date = Date(),
        leaseToken: String? = nil
    ) throws {
        guard Self.isSafeComponent(runID) else { throw WarmupOwnerError.invalidRunID }
        let leaseToken = leaseToken ?? UUID().uuidString
        guard Self.isSafeComponent(leaseToken) else { throw WarmupOwnerError.invalidLeaseToken }
        self.runID = runID
        self.leaseToken = leaseToken
        self.processIdentifier = processIdentifier
        self.startedAt = startedAt
    }

    public static func current(runID: String) throws -> Self {
        try Self(runID: runID, processIdentifier: ProcessInfo.processInfo.processIdentifier)
    }

    fileprivate static func isSafeComponent(_ value: String) -> Bool {
        !value.isEmpty && value.count <= 128 && value != "." && value != ".."
            && value.allSatisfy { $0.isNumber || $0.isLetter || "-_".contains($0) }
    }
}

public enum WarmupOwnerError: Error, Equatable, Sendable {
    case invalidRunID
    case invalidLeaseToken
    case pathOutsideRoot
    case ownerMismatch
    case symbolicLinkRejected
}

public struct WarmupOwnedTemporaryDirectory: Sendable {
    public let root: URL
    public let url: URL
    public let owner: WarmupProcessOwner
    private let ownerFileName = ".warmup-owner.json"

    init(root: URL, url: URL, owner: WarmupProcessOwner) { self.root = root; self.url = url; self.owner = owner }

    public static func create(
        under root: URL,
        owner: WarmupProcessOwner,
        fileManager: FileManager = .default
    ) throws -> Self {
        let root = root.standardizedFileURL
        let directory = root.appendingPathComponent(owner.runID, isDirectory: true)
        try Self.rejectSymlink(root, fileManager: fileManager, allowMissing: true)
        try fileManager.createDirectory(at: root, withIntermediateDirectories: true,
                                        attributes: [.posixPermissions: 0o700])
        try Self.rejectSymlink(root, fileManager: fileManager)
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: false,
                                        attributes: [.posixPermissions: 0o700])
        try Self.rejectSymlink(directory, fileManager: fileManager)
        let result = Self(root: root, url: directory, owner: owner)
        let ownerData = try JSONEncoder().encode(owner)
        try AtomicFileWriter.write(ownerData, to: result.ownerURL)
        return result
    }

    public var ownerURL: URL { self.url.appendingPathComponent(self.ownerFileName) }

    public func removeIfOwned(fileManager: FileManager = .default) throws {
        guard Self.isDescendant(self.url, of: self.root) else {
            throw WarmupOwnerError.pathOutsideRoot
        }
        try Self.rejectSymlink(self.root, fileManager: fileManager)
        try Self.rejectSymlink(self.url, fileManager: fileManager)
        try Self.rejectSymlink(self.ownerURL, fileManager: fileManager)
        let data = try Data(contentsOf: self.ownerURL)
        let recorded = try JSONDecoder().decode(WarmupProcessOwner.self, from: data)
        guard recorded == self.owner else { throw WarmupOwnerError.ownerMismatch }
        try fileManager.removeItem(at: self.url)
    }

    private static func isDescendant(_ url: URL, of root: URL) -> Bool {
        let rootPath = root.standardizedFileURL.path
        let path = url.standardizedFileURL.path
        return path.hasPrefix(rootPath + "/")
    }

    private static func rejectSymlink(
        _ url: URL,
        fileManager: FileManager,
        allowMissing: Bool = false
    ) throws {
        var ancestor = url.standardizedFileURL
        while ancestor.path != "/" {
            if (try? ancestor.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw WarmupOwnerError.symbolicLinkRejected
            }
            ancestor.deleteLastPathComponent()
        }
        guard allowMissing || fileManager.fileExists(atPath: url.path) else { throw WarmupOwnerError.pathOutsideRoot }
    }
}

public enum WarmupProcessExitStatus: Codable, Equatable, Sendable {
    case running
    case exited(code: Int32)
    case timedOut
    case cancelled
    case spawnFailed
    case unknown

    public var isSuccessful: Bool {
        if case .exited(let code) = self { return code == 0 }
        return false
    }
}

public struct WarmupProcessOutcome: Codable, Equatable, Sendable {
    public let status: WarmupProcessExitStatus
    public let stdoutBytes: Int
    public let stderrBytes: Int

    public init(status: WarmupProcessExitStatus, stdoutBytes: Int = 0, stderrBytes: Int = 0) {
        self.status = status
        self.stdoutBytes = max(0, stdoutBytes)
        self.stderrBytes = max(0, stderrBytes)
    }
}
