import AppKit
import CodexProfileCore
import Foundation
import Darwin

struct WarmupTransport: Sendable {
    typealias BeforeSend = @Sendable () async throws -> Void
    var observe: @Sendable (Data, URL) async throws -> WarmupQuotaObservation
    var send: @Sendable (Data, String, [WarmupQuotaObservation], URL, @escaping BeforeSend) async throws -> WarmupSendResult
    static let live = Self(observe: { data, root in
        try await CLIWarmupFetcher.observe(authData: data, tempRoot: root, clientVersion: AppInfo.version)
    }, send: { data, identity, baselines, root, before in
        try await CLIWarmupFetcher.sendMinimal(authData: data, expectedIdentity: identity, baselines: baselines,
            tempRoot: root, clientVersion: AppInfo.version, onBeforeSend: before)
    })
}

struct WarmupTiming: Sendable {
    var baselineGap: TimeInterval = 5
    var verificationOffsets: [TimeInterval] = [15, 65]
    var preparationBudget: TimeInterval = 120
    var batchBudget: TimeInterval = 180
}

/// One app-owned scheduler; views only edit drafts and observe this service.
@MainActor
final class WarmupService {
    typealias AuthProvider = @MainActor (String) async throws -> Data
    private let viewModel: WarmupViewModel
    private let store: CodexProfileCore.WarmupStore
    private let paths: AppPaths
    private let profiles: () -> [WarmupProfileItem]
    private let auth: AuthProvider
    private let canStart: () -> Bool
    private let transport: WarmupTransport
    private let timing: WarmupTiming
    private var timer: Timer?
    private var task: Task<Void, Never>?
    private var observers: [NSObjectProtocol] = []
    private var started = false
    private var lastMaintenance = Date.distantPast
    private var instanceLock: Int32 = -1
    private var ownerReady = false
    private var unsafeToMutateAccounts = false
    private var stopping = false
    private var memoryAvailable = true
    private var pressureSource: DispatchSourceMemoryPressure?
    var onChange: (() -> Void)?

    init(viewModel: WarmupViewModel, paths: AppPaths = AppPaths(),
         profiles: @escaping () -> [WarmupProfileItem], canStart: @escaping () -> Bool = { true },
         transport: WarmupTransport = .live, timing: WarmupTiming = WarmupTiming(),
         auth: @escaping AuthProvider) {
        self.viewModel = viewModel; self.paths = paths
        self.store = CodexProfileCore.WarmupStore(paths: paths)
        self.profiles = profiles; self.auth = auth
        self.canStart = canStart; self.transport = transport; self.timing = timing
        viewModel.onSave = { [weak self] plan in
            guard let self else { throw CancellationError() }
            guard self.ownerReady, !self.stopping else { throw CLIWorkCoordinator.Failure.unreapedChild }
            let control = try self.store.replacePlan(plan)
            self.armScheduler()
            self.onChange?()
            guard let saved = control.plan else { throw CancellationError() }
            return saved
        }
        viewModel.onRun = { [weak self] ids in self?.runNow(profileIDs: ids) }
        viewModel.onRecheck = { [weak self] in self?.recheck() }
    }

    func start() {
        guard !started else { return }; started = true
        do {
            _ = try store.load() // validates every ancestor before creating/opening a lock
            try AtomicFileWriter.ensurePrivateDirectory(store.rootURL)
            instanceLock = open(store.rootURL.appendingPathComponent("runtime.lock").path, O_RDWR | O_CREAT | O_NOFOLLOW, 0o600)
            guard instanceLock >= 0, flock(instanceLock, LOCK_EX | LOCK_NB) == 0 else {
                unsafeToMutateAccounts = true
                throw CodexRPCError.requestFailed("另一实例正在管理预热，请使用现有窗口")
            }
            let blocked = store.rootURL.appendingPathComponent("blocked-child.json")
            if FileManager.default.fileExists(atPath: blocked.path) {
                guard (try blocked.resourceValues(forKeys: [.isSymbolicLinkKey])).isSymbolicLink != true else { throw WarmupOwnerError.symbolicLinkRejected }
                let pid = try JSONDecoder().decode(Int32.self, from: Data(contentsOf: blocked))
                guard kill(pid, 0) != 0 && errno == ESRCH else {
                    unsafeToMutateAccounts = true
                    Task { await CLIWorkCoordinator.shared.blockFurtherWork() }
                    throw CLIWorkCoordinator.Failure.unreapedChild
                }
                try FileManager.default.removeItem(at: blocked)
            }
            ownerReady = true
            let pressure = DispatchSource.makeMemoryPressureSource(eventMask: [.normal, .warning, .critical], queue: .main)
            pressure.setEventHandler { [weak self, weak pressure] in
                Task { @MainActor in
                    guard let self, let pressure else { return }
                    self.memoryAvailable = !pressure.data.contains(.critical)
                }
            }
            pressure.activate(); pressureSource = pressure
            let recovered = try store.recoverInterruptedRuns()
            viewModel.loadSavedPlan(recovered.plan)
            refreshProfiles()
            showRows(recovered.runs)
            maintenance()
            armScheduler()
        } catch {
            ownerReady = false
            if instanceLock >= 0 { close(instanceLock); instanceLock = -1 }
            viewModel.setControlLoadError(safe(error))
        }
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.armScheduler() }
        })
        observers.append(NotificationCenter.default.addObserver(forName: Notification.Name("NSSystemClockDidChangeNotification"), object: nil, queue: .main) { [weak self] _ in
            Task { @MainActor in self?.armScheduler() }
        })
    }

    func stop() {
        stopping = true
        pressureSource?.cancel(); pressureSource = nil
        timer?.invalidate(); timer = nil; task?.cancel()
        for observer in observers {
            NotificationCenter.default.removeObserver(observer)
            NSWorkspace.shared.notificationCenter.removeObserver(observer)
        }
        observers.removeAll()
    }

    deinit { if instanceLock >= 0 { close(instanceLock) } }

    func refreshProfiles() { viewModel.profiles = profiles() }

    var menuTitle: String {
        if viewModel.isRunning { return "定时预热… · 正在执行" }
        if let date = viewModel.nextExecutionAt { return "定时预热… · \(WarmupViewModel.display(date))" }
        return "定时预热… · 未启用"
    }

    func cancelForAccountMutation() async throws {
        task?.cancel()
        await task?.value
        guard !unsafeToMutateAccounts else { throw CLIWorkCoordinator.Failure.unreapedChild }
    }

    func shutdown() async { stopping = true; stop(); await task?.value }
    var isRunning: Bool { viewModel.isRunning }
    func waitUntilIdle() async { await task?.value }

    private func armScheduler() {
        timer?.invalidate(); timer = nil
        guard ownerReady, !stopping, !viewModel.isRunning else { return }
        do {
            let control = try store.load()
            guard let plan = control.plan,
                  let occurrence = try WarmupPlanner.nextOccurrence(after: Date().addingTimeInterval(0.01), plan: plan) else {
                viewModel.nextExecutionAt = nil; onChange?(); return
            }
            viewModel.nextExecutionAt = occurrence.triggerDate
            let nextTimer = Timer(fire: max(Date(), occurrence.triggerDate.addingTimeInterval(-60)), interval: 0, repeats: false) { [weak self] _ in
                Task { @MainActor in
                    guard let self else { return }
                    if Date().timeIntervalSince(occurrence.triggerDate) <= 30 {
                        self.begin(profileIDs: plan.profileIDs, occurrence: occurrence)
                    } else {
                        self.viewModel.lastMessage = "本次时点已错过，未补发"
                        self.armScheduler()
                    }
                }
            }
            RunLoop.main.add(nextTimer, forMode: .common); timer = nextTimer
            onChange?()
        } catch { viewModel.setControlLoadError(safe(error)) }
    }

    private func runNow(profileIDs: [String]) { begin(profileIDs: profileIDs, occurrence: nil) }

    private func begin(profileIDs: [String], occurrence: WarmupOccurrence?) {
        guard memoryAvailable else { viewModel.lastMessage = "系统内存压力较高，暂未启动预热"; return }
        guard ownerReady, !stopping, canStart(), task == nil, !viewModel.isRunning, viewModel.controlLoadError == nil else { return }
        guard (1...4).contains(profileIDs.count) else { return }
        viewModel.isRunning = true
        viewModel.lastMessage = "正在读取真实窗口；已有窗口将跳过"
        viewModel.results = []
        onChange?()
        task = Task { [weak self] in
            guard let self else { return }
            defer {
                self.task = nil; self.viewModel.isRunning = false
                self.maintenance(); self.armScheduler(); self.onChange?()
            }
            do {
                try await self.execute(profileIDs: profileIDs, occurrence: occurrence)
            } catch {
                self.viewModel.lastMessage = error is CancellationError ? "执行已停止，已发出部分只保留核验记录" : self.safe(error)
                self.retirePending()
            }
        }
    }

    private struct Candidate: Sendable {
        let id: String
        let identity: String
        let baselines: [WarmupQuotaObservation]
        let key: WarmupExecutionKey
        let revision: Int
    }

    private struct SendOutcome: Sendable {
        let candidate: Candidate
        let result: WarmupSendResult?
        let error: String?
        let existingReset: Date?
    }

    private func execute(profileIDs: [String], occurrence: WarmupOccurrence?) async throws {
        let current = try store.load()
        let trigger = occurrence?.triggerDate ?? Date()
        let revision = occurrence?.planRevision ?? current.planRevision
        let planID = occurrence?.planID ?? "manual"
        let preparationBudget = min(timing.preparationBudget, occurrence.map { max(0, $0.triggerDate.timeIntervalSinceNow + 30) } ?? timing.preparationBudget)
        let prepareDeadline = ContinuousClock.now.advanced(by: .seconds(preparationBudget))
        let allowed = Set(profiles().filter(\.isAvailable).map(\.id))
        var candidates: [Candidate] = [], seen = Set<String>()
        for id in profileIDs {
            try Task.checkCancellation()
            guard canStart(), ownerReady else { throw CancellationError() }
            guard allowed.contains(id) else { appendRow(id, "不可用", nil, "账号已移除或需要重新登录"); continue }
            do {
                let data = try await bounded(deadline: prepareDeadline) { [self] in try await self.auth(id) }
                guard let identity = WarmupAccountIdentity.fingerprint(from: data) else { throw AuthError.missingTokens }
                guard seen.insert(identity).inserted else { appendRow(id, "重复账号", nil, "同一账号只处理一次"); continue }
                // Each read has a fresh lease and permit. The five-second gap
                // and the scheduled wait keep neither a CLI nor an account lease.
                let first = try await readWithOrdinaryPermit(id: id, identity: identity, deadline: prepareDeadline)
                try await Task.sleep(for: .seconds(timing.baselineGap))
                let second = try await readWithOrdinaryPermit(id: id, identity: identity, deadline: prepareDeadline)
                let key = WarmupExecutionKey(planID: planID, triggerDate: trigger, profileID: id, accountIdentity: identity)
                candidates.append(Candidate(id: id, identity: identity, baselines: [first, second], key: key, revision: revision))
                appendRow(id, "已准备", nil, "触发前还会核对当前窗口")
            } catch {
                if error is CancellationError || !ownerReady { throw error }
                appendRow(id, "未发送", nil, safe(error))
            }
        }
        if let occurrence, occurrence.triggerDate > Date() {
            try await Task.sleep(for: .seconds(occurrence.triggerDate.timeIntervalSinceNow))
        }
        try WarmupExecutionGate.validate(trigger: occurrence?.triggerDate)
        guard !candidates.isEmpty else { viewModel.lastMessage = "检查完成，没有可安全发送的账号"; return }
        // All network setup, send, and verification from this gate share one
        // monotonic budget. Acquiring a batch never nests an ordinary permit.
        let deadline = ContinuousClock.now.advanced(by: .seconds(timing.batchBudget))
        let permit = try await bounded(deadline: deadline) { try await CLIWorkCoordinator.shared.acquireBatch() }
        do {
            try WarmupExecutionGate.validate(trigger: occurrence?.triggerDate)
            try await sendAndVerify(candidates, scheduledTrigger: occurrence?.triggerDate, deadline: deadline)
            await CLIWorkCoordinator.shared.release(permit)
        } catch {
            await CLIWorkCoordinator.shared.release(permit)
            throw error
        }
    }

    private func sendAndVerify(_ candidates: [Candidate], scheduledTrigger: Date?, deadline: ContinuousClock.Instant) async throws {
        let outcomes = await withTaskGroup(of: SendOutcome.self) { group in
            for candidate in candidates {
                group.addTask { [self] in await self.send(candidate, scheduledTrigger: scheduledTrigger, deadline: deadline) }
            }
            var results: [SendOutcome] = []
            for await outcome in group { results.append(outcome) }
            return results
        }
        guard ownerReady else { throw CLIWorkCoordinator.Failure.unreapedChild }
        var sent: [SendOutcome] = []
        var samples: [String: [WarmupQuotaObservation]] = [:]
        for outcome in outcomes {
            let candidate = outcome.candidate
            if let reset = outcome.existingReset {
                _ = try store.recordOutcome(key: candidate.key, planRevision: candidate.revision, identityVersion: candidate.identity,
                    status: .existingWindow, reason: "触发前已存在活动窗口，本轮未发送")
                try store.saveEvidence(key: candidate.key, baseline: candidate.baselines.last, samples: [], resetAt: reset)
                appendRow(candidate.id, "已有窗口", reset, "本轮未发送")
                continue
            }
            // Only a durable sending intent can enter verification. Failures
            // before the callback are no-send outcomes, never fake completions.
            guard try store.load().runs.contains(where: { $0.key == candidate.key && !$0.status.isTerminal }) else {
                appendRow(candidate.id, "未发送", nil, outcome.error ?? "发送前检查未通过")
                continue
            }
            sent.append(outcome)
            samples[candidate.id] = outcome.result?.firstObservation.map { [$0] } ?? []
            if outcome.result?.completed == true {
                _ = try store.update(key: candidate.key, identityVersion: candidate.identity, status: .requestCompleted)
                _ = try store.update(key: candidate.key, identityVersion: candidate.identity, status: .verifying)
                appendRow(candidate.id, "正在确认", nil, "请求已完成；发送进程已退出")
            } else { appendRow(candidate.id, "请求结果待确认", nil, outcome.error ?? "未补发") }
        }
        let verificationStart = ContinuousClock.now
        for offset in timing.verificationOffsets where !sent.isEmpty {
            let target = verificationStart.advanced(by: .seconds(offset))
            guard target < deadline else { break }
            try await Task.sleep(until: target, clock: .continuous)
            for outcome in sent {
                try Task.checkCancellation()
                guard ContinuousClock.now < deadline else { break }
                let candidate = outcome.candidate
                do {
                    let sample = try await readLeased(id: candidate.id, identity: candidate.identity, deadline: deadline)
                    samples[candidate.id, default: []].append(sample)
                } catch {
                    if error is CancellationError || !ownerReady { throw error }
                    appendRow(candidate.id, "正在确认", nil, safe(error))
                }
            }
        }
        var confirmed = 0
        for outcome in sent {
            let candidate = outcome.candidate
            let evidence = samples[candidate.id] ?? []
            let verdict = WarmupQuotaConfirmation.evaluate(baseline: candidate.baselines[1], samples: evidence)
            var reset: Date?, status: WarmupRunStatus = .unknown
            if let result = outcome.result, result.completed, case .confirmed(_, let actualReset) = verdict,
               actualReset > Date(),
               actualReset.addingTimeInterval(-18000) >= result.sentAt.addingTimeInterval(-15),
               actualReset.addingTimeInterval(-18000) <= result.completedAt.addingTimeInterval(15) {
                status = .confirmed; reset = actualReset
            }
            // Storage failures must not paint a green success in the window.
            try store.saveEvidence(key: candidate.key, baseline: candidate.baselines.last, samples: evidence, resetAt: reset)
            _ = try store.update(key: candidate.key, identityVersion: candidate.identity, status: status,
                reason: status == .confirmed ? nil : (outcome.error ?? "核验证据不足；未补发"))
            if status == .confirmed { confirmed += 1 }
            appendRow(candidate.id, status == .confirmed ? "服务器窗口已确认" : "未确认", reset,
                status == .confirmed ? "跨进程额度读取已确认固定时间" : "只可重新检查，不自动补发")
        }
        try store.compact()
        try archiveResults()
        viewModel.lastMessage = "\(confirmed)/\(sent.count) 个发送后窗口已确认；已有窗口单独列出"
    }

    private func send(_ candidate: Candidate, scheduledTrigger: Date?, deadline: ContinuousClock.Instant) async -> SendOutcome {
        do {
            let result = try await bounded(deadline: deadline) { [self] in
                try Task.checkCancellation()
                let lease = try await self.claimLease(id: candidate.id, identity: candidate.identity)
                do {
                    let data = try await self.freshAuth(id: candidate.id, identity: candidate.identity)
                    let result = try await self.transport.send(data, candidate.identity, candidate.baselines,
                        self.paths.tempRoot.appendingPathComponent("warmup"), { [weak self, store = self.store] in
                            try Task.checkCancellation()
                            try WarmupExecutionGate.validate(trigger: scheduledTrigger)
                            guard ContinuousClock.now < deadline else { throw CodexRPCError.timeout(method: "预热批次") }
                            guard let self, await self.isDispatchAllowed() else { throw CancellationError() }
                            _ = try store.claimSending(key: candidate.key, planRevision: candidate.revision, identityVersion: candidate.identity)
                        })
                    try lease.release()
                    return result
                } catch {
                    if await self.quarantineIfNeeded(error, lease: lease) { throw error }
                    try? lease.release()
                    throw error
                }
            }
            return SendOutcome(candidate: candidate, result: result, error: nil, existingReset: nil)
        } catch CLIWarmupError.existingWindow(let reset) {
            return SendOutcome(candidate: candidate, result: nil, error: nil, existingReset: reset)
        } catch { return SendOutcome(candidate: candidate, result: nil, error: safe(error), existingReset: nil) }
    }

    private func isDispatchAllowed() -> Bool { ownerReady && memoryAvailable && !stopping && canStart() }

    private func readWithOrdinaryPermit(id: String, identity: String, deadline: ContinuousClock.Instant) async throws -> WarmupQuotaObservation {
        let permit = try await bounded(deadline: deadline) { try await CLIWorkCoordinator.shared.acquireOrdinary() }
        do {
            let result = try await readLeased(id: id, identity: identity, deadline: deadline)
            await CLIWorkCoordinator.shared.release(permit)
            return result
        } catch { await CLIWorkCoordinator.shared.release(permit); throw error }
    }

    private func readLeased(id: String, identity: String, deadline: ContinuousClock.Instant) async throws -> WarmupQuotaObservation {
        try await bounded(deadline: deadline) { [self] in
            try Task.checkCancellation()
            let lease = try await self.claimLease(id: id, identity: identity)
            do {
                let result = try await self.observe(id: id, identity: identity)
                try lease.release()
                return result
            } catch {
                if await self.quarantineIfNeeded(error, lease: lease) { throw error }
                try? lease.release()
                throw error
            }
        }
    }

    private func quarantineIfNeeded(_ error: Error, lease: WarmupAccountLease) async -> Bool {
        guard case CLIWarmupError.processDidNotExit(let pid) = error else { return false }
        ownerReady = false; unsafeToMutateAccounts = true
        try? lease.quarantine(ownedHome: paths.tempRoot.appendingPathComponent("warmup").appendingPathComponent("quarantine-" + UUID().uuidString))
        try? AtomicFileWriter.write(JSONEncoder().encode(pid), to: store.rootURL.appendingPathComponent("blocked-child.json"))
        await CLIWorkCoordinator.shared.blockFurtherWork()
        viewModel.setControlLoadError(safe(error))
        return true
    }

    private func bounded<T: Sendable>(deadline: ContinuousClock.Instant, operation: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await operation() }
            group.addTask { try await Task.sleep(until: deadline, clock: .continuous); throw CodexRPCError.timeout(method: "预热批次") }
            defer { group.cancelAll() }
            guard let result = try await group.next() else { throw CancellationError() }
            return result
        }
    }

    private func freshAuth(id: String, identity: String) async throws -> Data {
        let data = try await auth(id)
        guard WarmupAccountIdentity.fingerprint(from: data) == identity else { throw CodexRPCError.requestFailed("账号身份已变化，拒绝使用旧结果") }
        return data
    }

    private func observe(id: String, identity: String) async throws -> WarmupQuotaObservation {
        let data = try await freshAuth(id: id, identity: identity)
        let sample = try await transport.observe(data, paths.tempRoot.appendingPathComponent("warmup"))
        guard sample.accountIdentity == identity else { throw CodexRPCError.requestFailed("额度账号身份不匹配") }
        return sample
    }

    private func claimLease(id: String, identity: String) async throws -> WarmupAccountLease {
        // Also reserve aliases of the same saved identity, so renewal cannot
        // operate through another card while this account's CLI is running.
        var aliases = [id]
        for profile in profiles() where profile.id != id {
            if let data = try? await auth(profile.id), WarmupAccountIdentity.fingerprint(from: data) == identity { aliases.append(profile.id) }
        }
        let paths = paths
        return try await Task.detached { try WarmupAccountLease.acquire(profileIDs: aliases, paths: paths) }.value
    }

    private func appendRow(_ id: String, _ title: String, _ reset: Date?, _ detail: String) {
        let label = profiles().first(where: { $0.id == id })?.label ?? "账号 \(id)"
        let row = WarmupResultRow(id: id, label: label, statusTitle: title, resetAt: reset, detail: detail)
        if let index = viewModel.results.firstIndex(where: { $0.id == id }) { viewModel.results[index] = row }
        else { viewModel.results.append(row) }
        onChange?()
    }

    private func retirePending() {
        _ = try? store.recoverInterruptedRuns()
        _ = try? store.compact()
    }

    private func showRows(_ runs: [WarmupRunState]) {
        let latest = Dictionary(grouping: runs, by: { $0.key.profileID })
        viewModel.results = []
        for id in latest.keys.sorted() {
            guard let run = latest[id]?.max(by: { $0.updatedAt < $1.updatedAt }) else { continue }
            let expired = run.resetAt.map { $0 <= Date() } ?? false
            let title = run.status == .confirmed ? (expired ? "历史已确认 · 已到期" : "服务器窗口已确认") : run.status == .existingWindow ? (expired ? "历史窗口 · 已到期" : "已有窗口") : "未确认"
            appendRow(id, title, run.resetAt, run.reason ?? "最近一次保存的历史结果")
        }
    }

    private func recheck() {
        guard ownerReady, !stopping, canStart(), task == nil, !viewModel.isRunning, viewModel.controlLoadError == nil else { return }
        viewModel.isRunning = true
        task = Task { [weak self] in
            guard let self else { return }
            defer { self.task = nil; self.viewModel.isRunning = false; self.armScheduler() }
            do {
                let control = try self.store.load()
                let latest = Dictionary(grouping: control.runs, by: { $0.key.profileID })
                let ids = self.viewModel.results.map(\.id).prefix(4)
                let deadline = ContinuousClock.now.advanced(by: .seconds(120))
                for id in ids {
                    try Task.checkCancellation()
                    guard let run = latest[id]?.max(by: { $0.updatedAt < $1.updatedAt }) else { continue }
                    let sample = try await self.readWithOrdinaryPermit(id: id, identity: run.key.accountIdentity, deadline: deadline)
                    let window = sample.windows.first { $0.poolID == "codex" && $0.windowDurationMinutes == 300 }
                    self.appendRow(id, "已复查服务器报告", window?.resetsAt,
                        "仅查询；单次读数不证明窗口已启动，没有补发请求")
                }
                self.viewModel.lastMessage = "复查完成，没有补发请求"
            } catch { self.viewModel.lastMessage = self.safe(error) }
        }
    }

    private func archiveResults() throws {
        let runs = try store.load().runs
        let directory = store.rootURL.appendingPathComponent("results")
        _ = try WarmupMaintenance.pruneResults(root: store.rootURL)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        try AtomicFileWriter.write(try encoder.encode(runs), to: directory.appendingPathComponent(UUID().uuidString + ".json"))
    }

    private func maintenance() {
        // Capacity is enforced after every batch as well as at startup.
        do {
            let removed = try WarmupMaintenance.pruneResults(root: store.rootURL)
            let temporary = try WarmupMaintenance.pruneTemporaryHomes(root: paths.tempRoot.appendingPathComponent("warmup"))
            try AppLogger.prepareFile()
            lastMaintenance = Date()
            viewModel.maintenanceSummary = "最近清理 \(removed) 份结果、\(temporary.removed) 个空临时目录；\(temporary.retainedForManualReview) 个异常目录保留待检查"
        } catch { viewModel.maintenanceSummary = "维护未完成：\(safe(error))" }
    }

    private func safe(_ error: Error) -> String { LogRedactor.excerpt(error.localizedDescription, maxLength: 220) }
}
