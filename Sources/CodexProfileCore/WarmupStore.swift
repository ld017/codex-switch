import Foundation
import Darwin

public enum WarmupStoreError: LocalizedError, Equatable {
    case corruptControl(URL)
    case unsupportedSchema(Int)
    case planRevisionConflict(expected: Int, actual: Int)
    case alreadyClaimed(WarmupExecutionKey)
    case runNotFound(WarmupExecutionKey)
    case identityVersionMismatch
    case invalidTransition(from: WarmupRunStatus, to: WarmupRunStatus)

    public var errorDescription: String? {
        switch self {
        case .corruptControl(let url): return "预热控制文件损坏：\(url.path)"
        case .unsupportedSchema(let version): return "不支持的预热控制文件版本：\(version)"
        case .planRevisionConflict(let expected, let actual):
            return "预热计划已被修改（期望 revision \(expected)，实际 \(actual)）"
        case .alreadyClaimed: return "该预热执行键已经记录，禁止重复发送"
        case .runNotFound: return "找不到预热执行记录"
        case .identityVersionMismatch: return "账号身份版本已变化，拒绝写入旧执行结果"
        case .invalidTransition(let from, let to): return "非法预热状态迁移：\(from.rawValue) → \(to.rawValue)"
        }
    }
}

/// Small, transactional control state for scheduling. Detailed verification
/// evidence is deliberately kept outside this file so result cleanup cannot
/// erase the execution watermark or a pending send intent.
public final class WarmupStore: @unchecked Sendable {
    public let rootURL: URL
    public let controlURL: URL
    public let lockURL: URL

    private let encoder: JSONEncoder
    private let decoder: JSONDecoder
    private let localLock = NSLock()

    public init(rootURL: URL) {
        self.rootURL = rootURL
        self.controlURL = rootURL.appendingPathComponent("control.json")
        self.lockURL = rootURL.appendingPathComponent("control.lock")
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.sortedKeys]
        self.encoder = encoder
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        self.decoder = decoder
    }

    public convenience init(paths: AppPaths) {
        self.init(rootURL: paths.switcherHome.appendingPathComponent("warmup", isDirectory: true))
    }

    public func load() throws -> WarmupControl {
        self.localLock.lock()
        defer { self.localLock.unlock() }
        return try self.loadUnlocked()
    }

    public func recoverInterruptedRuns() throws -> WarmupControl {
        try mutate { control in
            for index in control.runs.indices where !control.runs[index].status.isTerminal {
                control.runs[index].status = .unknown
                control.runs[index].reason = "上次执行中断；禁止补发，可重新检查窗口"
                let key = control.runs[index].key
                control.executionWatermarks[key.watermarkKey] = max(control.executionWatermarks[key.watermarkKey] ?? .distantPast, key.triggerDate)
            }
            return control
        }
    }

    private func loadUnlocked() throws -> WarmupControl {
        try rejectSymlinks()
        guard FileManager.default.fileExists(atPath: controlURL.path) else {
            return WarmupControl()
        }
        do {
            guard (try controlURL.resourceValues(forKeys: [.fileSizeKey]).fileSize ?? 0) <= 512 * 1024 else {
                throw WarmupStoreError.corruptControl(controlURL)
            }
            let data = try Data(contentsOf: controlURL)
            let control = try decoder.decode(WarmupControl.self, from: data)
            guard control.schemaVersion <= WarmupControl.currentSchemaVersion else {
                throw WarmupStoreError.unsupportedSchema(control.schemaVersion)
            }
            if let plan = control.plan { try plan.validate() }
            return control
        } catch let error as WarmupStoreError {
            throw error
        } catch {
            throw WarmupStoreError.corruptControl(controlURL)
        }
    }

    /// Replaces the active plan in one locked transaction and advances the
    /// revision. A caller cannot accidentally reuse a stale plan revision.
    @discardableResult
    public func replacePlan(_ plan: WarmupPlan?) throws -> WarmupControl {
        if let plan { try plan.validate() }
        return try mutate { control in
            var next = plan
            control.planRevision += 1
            next?.revision = control.planRevision
            control.plan = next
            return control
        }
    }

    /// Records the sending intent before any network request. Existing runs or
    /// watermarks are authoritative, so a crash/restart cannot send the same
    /// execution key a second time.
    @discardableResult
    public func claimSending(
        key: WarmupExecutionKey,
        planRevision: Int,
        identityVersion: String,
        now: Date = Date()
    ) throws -> WarmupRunState {
        try claim(
            key: key,
            planRevision: planRevision,
            identityVersion: identityVersion,
            status: .sending,
            reason: nil,
            now: now)
    }

    /// Records a no-send terminal decision such as an already-active window,
    /// a missed slot, or a cancellation. It uses the same execution-key and
    /// watermark transaction as `claimSending`, so the scheduler cannot keep
    /// re-evaluating the same slot after a restart.
    @discardableResult
    public func recordOutcome(
        key: WarmupExecutionKey,
        planRevision: Int,
        identityVersion: String,
        status: WarmupRunStatus,
        reason: String? = nil,
        now: Date = Date()
    ) throws -> WarmupRunState {
        guard status.isTerminal else {
            throw WarmupStoreError.invalidTransition(from: .planned, to: status)
        }
        return try claim(
            key: key,
            planRevision: planRevision,
            identityVersion: identityVersion,
            status: status,
            reason: reason,
            now: now)
    }

    private func claim(
        key: WarmupExecutionKey,
        planRevision: Int,
        identityVersion: String,
        status: WarmupRunStatus,
        reason: String?,
        now: Date
    ) throws -> WarmupRunState {
        try mutate { control in
            guard control.planRevision == planRevision else {
                throw WarmupStoreError.planRevisionConflict(expected: planRevision, actual: control.planRevision)
            }
            if let existing = control.runs.first(where: {
                ($0.key.watermarkKey == key.watermarkKey && $0.key.triggerDate == key.triggerDate)
                || ($0.key.accountIdentity == key.accountIdentity && [.sending, .requestCompleted, .verifying, .unknown].contains($0.status)
                    && now.timeIntervalSince($0.createdAt) < 5 * 3600)
            }) {
                throw WarmupStoreError.alreadyClaimed(existing.key)
            }
            if let watermark = control.executionWatermarks[key.watermarkKey], watermark >= key.triggerDate {
                throw WarmupStoreError.alreadyClaimed(key)
            }
            let state = WarmupRunState(
                key: key,
                planRevision: planRevision,
                identityVersion: identityVersion,
                status: status,
                createdAt: now,
                updatedAt: now,
                reason: Self.boundedReason(reason))
            control.runs.append(state)
            if status.isTerminal {
                control.executionWatermarks[key.watermarkKey] = max(
                    control.executionWatermarks[key.watermarkKey] ?? .distantPast,
                    key.triggerDate)
            }
            return state
        }
    }

    @discardableResult
    public func update(
        key: WarmupExecutionKey,
        identityVersion: String,
        status: WarmupRunStatus,
        reason: String? = nil,
        now: Date = Date()
    ) throws -> WarmupRunState {
        try mutate { control in
            guard let index = control.runs.firstIndex(where: { $0.key == key }) else {
                throw WarmupStoreError.runNotFound(key)
            }
            var state = control.runs[index]
            guard state.identityVersion == identityVersion else {
                throw WarmupStoreError.identityVersionMismatch
            }
            guard state.status.allowsTransition(to: status) else {
                throw WarmupStoreError.invalidTransition(from: state.status, to: status)
            }
            state.status = status
            state.reason = Self.boundedReason(reason)
            state.updatedAt = now
            control.runs[index] = state
            if status.isTerminal {
                let watermarkKey = key.watermarkKey
                if control.executionWatermarks[watermarkKey].map({ $0 < key.triggerDate }) ?? true {
                    control.executionWatermarks[watermarkKey] = key.triggerDate
                }
            }
            return state
        }
    }

    /// Drops old terminal summaries while retaining the watermark that blocks
    /// duplicate sends. All active runs and unknown sends younger than five
    /// hours are retained independently of the display-history count.
    @discardableResult
    public func compact(keepTerminalPerWatermark: Int = 1, now: Date = Date()) throws -> WarmupControl {
        precondition(keepTerminalPerWatermark >= 0)
        return try mutate { control in
            var grouped: [String: [WarmupRunState]] = [:]
            for run in control.runs where run.status.isTerminal {
                grouped[run.key.watermarkKey, default: []].append(run)
            }
            let keep = Set(grouped.values
                .flatMap { $0.sorted { $0.updatedAt > $1.updatedAt }.prefix(keepTerminalPerWatermark) }
                .map(\.key.storageKey))
            control.runs.removeAll {
                let recentUnknown = $0.status == .unknown && now.timeIntervalSince($0.createdAt) < 5 * 3600
                return $0.status.isTerminal && !recentUnknown && !keep.contains($0.key.storageKey)
            }
            return control
        }
    }

    private func mutate<T>(_ body: (inout WarmupControl) throws -> T) throws -> T {
        self.localLock.lock()
        defer { self.localLock.unlock() }
        // Validate before CacheLock creates or opens its path, not only once
        // inside the lock: even a dangling link must not create its target.
        try rejectSymlinks()
        return try CacheLock.withLock(at: lockURL) {
            var control = try self.loadUnlocked()
            let value = try body(&control)
            try self.writeUnlocked(control)
            return value
        }
    }

    private func writeUnlocked(_ control: WarmupControl) throws {
        try rejectSymlinks()
        let data = try encoder.encode(control)
        guard data.count <= 512 * 1024 else { throw WarmupStoreError.corruptControl(controlURL) }
        try AtomicFileWriter.write(data, to: controlURL)
        let fd = open(rootURL.path, O_RDONLY)
        guard fd >= 0 else { throw POSIXError(.EIO) }
        defer { close(fd) }
        guard fsync(fd) == 0 else { throw POSIXError(.EIO) }
    }

    public func saveEvidence(key: WarmupExecutionKey, baseline: WarmupQuotaObservation?, samples: [WarmupQuotaObservation], resetAt: Date? = nil) throws {
        try mutate { control in
            guard let index = control.runs.firstIndex(where: { $0.key == key }) else { throw WarmupStoreError.runNotFound(key) }
            control.runs[index].baseline = baseline
            control.runs[index].samples = Array(samples.suffix(4))
            control.runs[index].resetAt = resetAt
        }
    }

    private func rejectSymlinks() throws {
        var parent = rootURL.standardizedFileURL
        while parent.path != "/" {
            if (try? parent.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw WarmupOwnerError.symbolicLinkRejected
            }
            parent.deleteLastPathComponent()
        }
        for file in [controlURL, lockURL] {
            if (try? file.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw WarmupOwnerError.symbolicLinkRejected
            }
        }
    }

    private static func boundedReason(_ reason: String?) -> String? {
        guard let reason else { return nil }
        var result = String()
        var bytes = 0
        for scalar in reason.unicodeScalars {
            let count = scalar.utf8.count
            guard bytes + count <= 2048 else { break }
            result.unicodeScalars.append(scalar)
            bytes += count
        }
        return result
    }
}
