import CodexProviderCore
import Foundation

@MainActor
protocol ConnectionStatusSource: AnyObject {
    func currentProvider() throws -> Provider
    func sub2APIIsOnline() async -> Bool
    func proxyEnvironmentConflicts() async -> [ProxyEnvironmentConflict]
    func clashReadinessStatus() async -> ClashReadinessStatus
    func sharedHistoryIsConfigured() async -> Bool
}

extension CodexSystem: ConnectionStatusSource {}

@MainActor
final class ConnectionStatusController {
    private enum Sampling: Int { case activeChannel, diagnostics }
    private let source: any ConnectionStatusSource
    private let now: () -> Date
    private(set) var snapshot = ConnectionStatusSnapshot()
    var onChange: ((ConnectionStatusSnapshot) -> Void)?

    private var generation: UInt = 0
    private var samplingTask: Task<Void, Never>?
    private var samplingKind: Sampling?
    private var samplingGeneration: UInt?
    private var pending: Sampling?
    private var stopped = false

    init(source: any ConnectionStatusSource, now: @escaping () -> Date = Date.init) {
        self.source = source
        self.now = now
    }

    func refreshProvider() {
        guard !stopped else { return }
        let provider: MenuProviderState
        do { provider = .configured(try source.currentProvider()) }
        catch { provider = .unreadable }
        if provider != snapshot.provider { clearCache(provider: provider) }
        emit()
    }

    func requestActiveChannelStatus() {
        guard !stopped, snapshot.provider == .configured(.sub2api),
              !ConnectionStatusSnapshot.isFresh(snapshot.sub2APICheckedAt, now: now()) else { return }
        request(.activeChannel)
    }

    func requestDiagnostics() {
        guard !stopped else { return }
        let date = now()
        guard !ConnectionStatusSnapshot.isFresh(snapshot.diagnosticsCheckedAt, now: date)
            || !ConnectionStatusSnapshot.isFresh(snapshot.sub2APICheckedAt, now: date) else { return }
        request(.diagnostics)
    }

    func invalidate() {
        guard !stopped else { return }
        clearCache(provider: snapshot.provider)
        emit()
    }

    func stop() {
        guard !stopped else { return }
        stopped = true
        clearCache(provider: snapshot.provider)
    }

    private func clearCache(provider: MenuProviderState) {
        generation &+= 1
        samplingTask?.cancel()
        // Cancellation does not release this slot. The source may be awaiting
        // detached process work; only finishSampling can start the next batch.
        pending = nil
        snapshot = ConnectionStatusSnapshot(provider: provider)
    }

    private func request(_ kind: Sampling) {
        if samplingTask != nil {
            if samplingGeneration != generation || (samplingKind == .activeChannel && kind == .diagnostics) {
                if pending == nil || kind.rawValue > pending!.rawValue { pending = kind }
                if kind == .diagnostics { snapshot.isRefreshingDiagnostics = true }
                else { snapshot.isRefreshingActiveChannel = true }
                emit()
            }
            return
        }
        startSampling(kind)
    }

    private func startSampling(_ kind: Sampling) {
        let batchGeneration = generation
        samplingKind = kind
        samplingGeneration = batchGeneration
        snapshot.isRefreshingActiveChannel = kind == .activeChannel
        snapshot.isRefreshingDiagnostics = kind == .diagnostics
        emit()
        samplingTask = Task { [weak self] in
            guard let self else { return }
            defer { self.finishSampling() }
            guard !Task.isCancelled else { return }
            let online = await self.source.sub2APIIsOnline()
            let portCheckedAt = self.now()
            guard self.canAccept(batchGeneration) else { return }
            if kind == .diagnostics {
                let conflicts = await self.source.proxyEnvironmentConflicts()
                guard self.canAccept(batchGeneration) else { return }
                let readiness = await self.source.clashReadinessStatus()
                guard self.canAccept(batchGeneration) else { return }
                let configured = await self.source.sharedHistoryIsConfigured()
                guard self.canAccept(batchGeneration) else { return }
                self.snapshot.proxyConflictVariables = conflicts.map(\.variable)
                self.snapshot.clashReadiness = readiness
                self.snapshot.sharedHistoryConfigured = configured
                self.snapshot.diagnosticsCheckedAt = self.now()
            }
            // Never assign provider from an asynchronous batch.
            self.snapshot.sub2APIOnline = online
            self.snapshot.sub2APICheckedAt = portCheckedAt
        }
    }

    private func canAccept(_ batchGeneration: UInt) -> Bool {
        !stopped && generation == batchGeneration && !Task.isCancelled
    }

    private func finishSampling() {
        samplingTask = nil
        samplingKind = nil
        samplingGeneration = nil
        guard !stopped else { return }
        snapshot.isRefreshingActiveChannel = false
        snapshot.isRefreshingDiagnostics = false
        let next = pending
        pending = nil
        emit()
        if let next { startSampling(next) }
    }

    private func emit() { onChange?(snapshot) }
}
