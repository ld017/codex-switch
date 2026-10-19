import Foundation
import CodexProfileCore
import CryptoKit

// MARK: - UsageProvider

@MainActor
final class UsageProvider {
    typealias Fetch = @Sendable (String, Data, URL, Bool,
        @escaping @Sendable (UsageSnapshot) async -> Void) async throws -> UsageSnapshot

    private let store: ProfileStore
    private let paths: AppPaths
    private let fetch: Fetch
    private let coordinator: CLIWorkCoordinator
    private var pendingForceRefresh = false
    private(set) var completedProfiles = 0
    private(set) var totalProfiles = 0
    private var refreshTask: Task<Void, Never>?
    private var refreshGeneration: UUID?
    private var pendingCancelledRefresh: Task<Void, Never>?
    private var lastRefreshAll: Date = .distantPast
    private(set) var isRefreshing = false
    /// Leases currently held by this refresh cycle, keyed by profile ID, so
    /// `cancelRefreshes()` can release them immediately instead of waiting on
    /// an in-flight task's own `defer` — cooperative cancellation means that
    /// defer only runs once the task next checks `Task.isCancelled`, which can
    /// lag a cancellation by a couple of seconds while a fetch is in flight.
    private var activeLeases: [String: String] = [:]
    /// The in-flight lease release started by `cancelRefreshes()`. Releasing is
    /// `async` (the flock must stay off the main thread), and `clearSavedAuth`
    /// cancels and then immediately requests a fresh refresh — so without this
    /// handle the new refresh can reach `claimLease` before the old lease row
    /// is gone and lose its own claim for the lease's full lifetime. Awaited
    /// before any claim, which makes the ordering deterministic.
    private var pendingLeaseRelease: Task<Void, Never>?
    var onChange: (() -> Void)?

    init(store: ProfileStore, coordinator: CLIWorkCoordinator = .shared,
         fetch: @escaping Fetch = { id, auth, config, includeTokens, onQuota in
        try await CLIUsageFetcher.fetch(profileId: id, authData: auth, codexConfigURL: config,
            clientVersion: AppInfo.version, includeTokenActivity: includeTokens, onQuota: onQuota)
    }) {
        self.store = store
        self.paths = store.usageCachePaths
        self.fetch = fetch
        self.coordinator = coordinator
    }

    func cancelRefreshes() {
        let cancelledActiveRefresh = self.refreshGeneration != nil
        self.pendingForceRefresh = false
        let cancelledTask = self.refreshTask
        cancelledTask?.cancel()
        if let cancelledTask {
            let previous = self.pendingCancelledRefresh
            self.pendingCancelledRefresh = Task {
                await previous?.value
                await cancelledTask.value
            }
        }
        self.refreshTask = nil
        self.refreshGeneration = nil
        self.isRefreshing = false
        // removeLease is `async` (it holds the flock, which must stay off the
        // main thread) and this method's callers are synchronous, so the
        // releases start here and are awaited at the next claim rather than
        // blocking this main-actor call. Chained onto any previous release so
        // two cancels in a row cannot reorder.
        let leasesToRelease = self.activeLeases
        self.activeLeases.removeAll()
        if !leasesToRelease.isEmpty {
            let previous = self.pendingLeaseRelease
            let paths = self.paths
            self.pendingLeaseRelease = Task {
                await previous?.value
                await cancelledTask?.value
                for (profileID, token) in leasesToRelease {
                    await Self.removeLease(profileIDs: [profileID], expectedToken: token, paths: paths)
                }
            }
        }
        if cancelledActiveRefresh {
            self.store.flushCacheIfDirty()
        }
    }

    func cancelAndWait() async {
        cancelRefreshes()
        await pendingCancelledRefresh?.value
        await pendingLeaseRelease?.value
    }

    func refreshAll(force: Bool = false) {
        if self.isRefreshing {
            self.pendingForceRefresh = self.pendingForceRefresh || force
            return
        }
        guard force || Date().timeIntervalSince(self.lastRefreshAll) > 60 else { return }

        // The nightly renewal LaunchAgent writes rejections to the shared cache
        // file from a separate process; re-read them at the start of every
        // cycle so this run's statuses reflect the current on-disk state.
        self.store.reloadRenewalStatesFromDisk()

        if !force,
           self.store.statuses.values.allSatisfy({
               switch $0 {
               case .notSetUp: return true
               default: return false
               }
           }) {
            return
        }

        self.lastRefreshAll = Date()
        self.isRefreshing = true
        let generation = UUID()
        self.refreshGeneration = generation
        self.completedProfiles = 0
        self.totalProfiles = self.store.config.profiles.count
        self.onChange?()

        let profiles = self.store.config.profiles
        let liveId = self.store.liveProfileId ?? ""
        let contexts = profiles.map { p in
            RefreshContext(id: p.id, cached: self.store.cache.snapshots[p.id], isLive: p.id == liveId)
        }
        let source = self.store.usageAuthSource()

        let pendingCancellation = self.pendingCancelledRefresh
        self.refreshTask = Task { @MainActor in
            // Cancellation can need time to shut down its CLI. A new cycle
            // must not overlap that teardown, even if its generation is newer.
            await pendingCancellation?.value
            guard !Task.isCancelled, self.refreshGeneration == generation else { return }
            self.pendingCancelledRefresh = nil
            // One short-lived CLI at a time limits peak memory. Publish each quota
            // immediately, so a slow token endpoint or another account cannot hide it.
            let groups = await Self.groupContexts(contexts, source: source)
            for fetchGroup in groups {
                guard !Task.isCancelled, self.refreshGeneration == generation else { break }
                await self.refreshGroup(fetchGroup, generation: generation, source: source, force: force)
                guard !Task.isCancelled, self.refreshGeneration == generation else { break }
                self.completedProfiles += fetchGroup.contexts.count
                self.onChange?()
            }
            if self.refreshGeneration == generation {
                self.isRefreshing = false
                self.refreshTask = nil
                self.refreshGeneration = nil
                self.store.flushCacheIfDirty()
                self.onChange?()
                if self.pendingForceRefresh {
                    self.pendingForceRefresh = false
                    self.refreshAll(force: true)
                }
            }
        }
    }

    static func needsTokenRefresh(cached: UsageSnapshot?, force: Bool, now: Date = Date()) -> Bool {
        guard !force, let cached, cached.tokenActivityUnavailable != true,
              let activity = cached.tokenActivity,
              activity.lifetimeTokens != nil, activity.dailyUsageBuckets != nil else { return true }
        let age = now.timeIntervalSince(activity.fetchedAt)
        return age < 0 || age >= 900
    }

    static func mergingTokenActivity(_ snapshot: UsageSnapshot, cached: UsageSnapshot?) -> UsageSnapshot {
        var result = snapshot
        guard let old = cached?.tokenActivity else { return result }
        guard let fresh = snapshot.tokenActivity else {
            result.tokenActivity = old
            result.tokenActivityUnavailable = true
            return result
        }
        let usesCached = (fresh.lifetimeTokens == nil && old.lifetimeTokens != nil)
            || (fresh.dailyUsageBuckets == nil && old.dailyUsageBuckets != nil)
        result.tokenActivity = TokenActivity(
            lifetimeTokens: fresh.lifetimeTokens ?? old.lifetimeTokens,
            dailyUsageBuckets: fresh.dailyUsageBuckets ?? old.dailyUsageBuckets,
            fetchedAt: usesCached ? min(fresh.fetchedAt, old.fetchedAt) : fresh.fetchedAt)
        if usesCached { result.tokenActivityUnavailable = true }
        return result
    }

    private struct RefreshContext {
        let id: String
        let cached: UsageSnapshot?
        let isLive: Bool
        /// This profile's own credential fingerprint (SHA-256(refresh token),
        /// first 12 hex chars — matches the CLI's `renewalCredentialFingerprint`),
        /// computed from its individually-loaded auth blob before batching by
        /// identity. Used to detect that a persisted rejection's credential has
        /// been replaced. Nil when the auth blob could not be loaded or parsed.
        var credentialFingerprint: String? = nil
    }

    private struct RefreshGroup {
        var contexts: [RefreshContext]
        let authData: Data?
        let loadError: (any Error)?
    }

    private nonisolated static func groupContexts(
        _ contexts: [RefreshContext], source: ProfileStore.UsageAuthSource
    ) async -> [RefreshGroup] {
        var groups: [String: RefreshGroup] = [:]
        for var context in contexts {
            var loadError: (any Error)?
            var authData: Data?
            do {
                authData = try await Self.loadAuthData(
                    source: source, profileID: context.id, isLive: context.isLive)
            } catch {
                loadError = error
            }
            context.credentialFingerprint = authData.flatMap(Self.credentialFingerprint(from:))
            // Group by credential fingerprint, not identity: this batches
            // per-profile usage/health refreshes, and two profiles sharing an
            // account identity can still hold different credentials. Batching
            // by identity would fetch one profile's usage and apply it to a
            // sibling profile with a different refresh token.
            let key = context.credentialFingerprint ?? "profile:\(context.id)"
            if var group = groups[key] {
                group.contexts.append(context)
                groups[key] = group
            } else {
                groups[key] = RefreshGroup(contexts: [context], authData: authData, loadError: loadError)
            }
        }
        return groups.values.sorted {
            let leftLive = $0.contexts.contains(where: \.isLive)
            let rightLive = $1.contexts.contains(where: \.isLive)
            if leftLive != rightLive { return leftLive }
            return ($0.contexts.first?.id ?? "") < ($1.contexts.first?.id ?? "")
        }
    }

    /// Mirrors the CLI's `renewalCredentialFingerprint`: SHA-256 of the refresh
    /// token, first 12 hex characters. Used to detect when a profile's stored
    /// credential has moved past the one a persisted rejection condemned.
    private nonisolated static func credentialFingerprint(from authData: Data) -> String? {
        guard let credentials = try? AuthBlob.load(from: authData), !credentials.refreshToken.isEmpty else {
            return nil
        }
        let digest = SHA256.hash(data: Data(credentials.refreshToken.utf8))
            .map { String(format: "%02x", $0) }
            .joined()
        return String(digest.prefix(12))
    }

    private func refreshGroup(
        _ fetchGroup: RefreshGroup, generation: UUID, source: ProfileStore.UsageAuthSource, force: Bool
    ) async {
        do {
            let permit = try await self.coordinator.acquireOrdinary()
            let freshGroups = await Self.groupContexts(fetchGroup.contexts, source: self.store.usageAuthSource())
            for fresh in freshGroups {
                await self.refreshGroupWithPermit(fresh, generation: generation, source: source, force: force)
            }
            await self.coordinator.release(permit)
        } catch { /* Cancellation while queued owns no child or lease. */ }
    }

    private func refreshGroupWithPermit(
        _ originalGroup: RefreshGroup, generation: UUID, source: ProfileStore.UsageAuthSource, force: Bool
    ) async {
        var fetchGroup = originalGroup
        guard !Task.isCancelled, self.refreshGeneration == generation else { return }
        guard !self.store.isAuthMutationInProgress() else { return }
        var diagnostics = Dictionary(uniqueKeysWithValues: fetchGroup.contexts.map {
            ($0.id, ProfileRefreshDiagnostics(lastAttemptAt: Date()))
        })

        func finalize(
            _ context: RefreshContext, _ status: ProfileStatus, decision: String
        ) async {
            diagnostics[context.id]?.lastDecision = decision
            await self.publish(context, status: status, diagnostics: diagnostics[context.id]!,
                generation: generation, source: source)
        }

        guard fetchGroup.authData != nil else {
            for context in fetchGroup.contexts {
                if let error = fetchGroup.loadError {
                    // Loading the auth blob threw. `.notFound` means genuinely no
                    // credential; any other error (e.g. a Keychain read failure)
                    // must not be reported as "not set up" — fall back to the
                    // last-known snapshot and surface the error, matching the
                    // pre-batching behavior this replaced.
                    diagnostics[context.id]?.lastError = error.localizedDescription
                    if let authError = error as? AuthError, case .notFound = authError {
                        await finalize(context, .notSetUp, decision: "not-set-up")
                    } else {
                        await finalize(context, .stale(context.cached), decision: "stale")
                        AppLogger.warning("Usage refresh failed",
                                          metadata: ["profile": context.id, "error": error.localizedDescription])
                    }
                } else {
                    await finalize(context, .notSetUp, decision: "not-set-up")
                }
            }
            return
        }

        let reservation = LeaseReservation(
            token: UUID().uuidString,
            home: "",
            expiresAt: Date().addingTimeInterval(60),
            createdAt: Date())
        await self.pendingLeaseRelease?.value
        guard !Task.isCancelled, self.refreshGeneration == generation else { return }
        guard await Self.claimLease(
            profileIDs: fetchGroup.contexts.map(\.id), reservation: reservation, paths: self.paths) else {
            for context in fetchGroup.contexts {
                diagnostics[context.id]?.lastError = "账号被其他任务保留，或缓存暂不可用"
                await finalize(context, .stale(context.cached), decision: "reserved")
            }
            return
        }
        guard !Task.isCancelled, self.refreshGeneration == generation else {
            await Self.removeLease(profileIDs: fetchGroup.contexts.map(\.id),
                expectedToken: reservation.token, paths: self.paths)
            return
        }
        for context in fetchGroup.contexts {
            self.activeLeases[context.id] = reservation.token
        }
        // `removeLease` is `async` (it holds `flock` and must stay off the
        // main thread), and `defer` cannot contain `await`, so the lease
        // release below is called explicitly on every exit path from this
        // point instead of via `defer`. There are exactly two: the
        // `CancellationError` early return, and falling off the end of the
        // `do`/`catch` below.

        do {
            // Renewal may have replaced tokens while this operation queued.
            // All affected cards are now leased; reload again before using any
            // credential, and re-group if aliases have diverged.
            let fresh = await Self.groupContexts(fetchGroup.contexts, source: self.store.usageAuthSource())
            if fresh.count != 1 {
                await self.releaseLease(fetchGroup.contexts.map(\.id), expectedToken: reservation.token)
                for group in fresh { await self.refreshGroupWithPermit(group, generation: generation, source: source, force: force) }
                return
            }
            guard let group = fresh.first, let authData = group.authData else { throw AuthError.notFound }
            fetchGroup = group
            try Task.checkCancellation()
            let includeTokens = fetchGroup.contexts.contains {
                Self.needsTokenRefresh(cached: $0.cached, force: force)
            }
            let publishedContexts = fetchGroup.contexts
            let snapshot = try await self.fetch(fetchGroup.contexts[0].id, authData,
                self.store.codexConfigURL(), includeTokens, { quota in
                    for context in publishedContexts {
                        let cachedQuota = await Self.mergingTokenActivity(quota, cached: context.cached)
                        await self.publish(context, status: .available(cachedQuota),
                            diagnostics: ProfileRefreshDiagnostics(lastAttemptAt: Date(), lastDecision: "quota-ready"),
                            generation: generation, source: source)
                    }
                })
            for context in fetchGroup.contexts {
                diagnostics[context.id]?.lastError = nil
                var merged = Self.mergingTokenActivity(snapshot, cached: context.cached)
                if !includeTokens {
                    merged.tokenActivityUnavailable = context.cached?.tokenActivityUnavailable
                    merged.tokenActivityError = context.cached?.tokenActivityError
                }
                await finalize(context, .available(merged), decision: "available")
                AppLogger.info("Usage refresh succeeded", metadata: ["profile": context.id])
            }
        } catch is CancellationError {
            await self.releaseLease(fetchGroup.contexts.map(\.id), expectedToken: reservation.token)
            return
        } catch let error as CodexRPCError where error.isAuthRequired {
            for context in fetchGroup.contexts {
                diagnostics[context.id]?.lastError = error.localizedDescription
                await finalize(context, .reloginNeeded(context.cached), decision: "relogin-needed")
                AppLogger.warning("Usage refresh requires re-login",
                                  metadata: ["profile": context.id, "error": error.localizedDescription])
            }
        } catch let error as AuthError {
            for context in fetchGroup.contexts {
                diagnostics[context.id]?.lastError = error.localizedDescription
                if case .notFound = error {
                    await finalize(context, .notSetUp, decision: "not-set-up")
                } else {
                    await finalize(context, .stale(context.cached), decision: "stale")
                    AppLogger.warning("Usage refresh failed",
                                      metadata: ["profile": context.id, "error": error.localizedDescription])
                }
            }
        } catch {
            for context in fetchGroup.contexts {
                diagnostics[context.id]?.lastError = error.localizedDescription
                await finalize(context, .stale(context.cached), decision: "stale")
                AppLogger.warning("Usage refresh failed",
                                  metadata: ["profile": context.id, "error": error.localizedDescription])
            }
        }
        await self.releaseLease(fetchGroup.contexts.map(\.id), expectedToken: reservation.token)
    }

    private func publish(_ context: RefreshContext, status: ProfileStatus,
        diagnostics: ProfileRefreshDiagnostics, generation: UUID, source: ProfileStore.UsageAuthSource
    ) async {
        // Bail if the refresh was cancelled (e.g. by cancelRefreshes() during
        // a profile switch). Prevents in-flight stale writes from landing.
        guard !Task.isCancelled, self.refreshGeneration == generation else { return }

        self.store.clearRenewalStateIfCredentialMoved(
            for: context.id, currentCredentialFingerprint: context.credentialFingerprint)

        // Re-derive liveness at finalize time. The snapshot was fetched under
        // whichever credentials were live at refresh START; if the live
        // profile changed mid-flight, this snapshot was read against the wrong
        // auth.json and must not be written under `id`. The post-switch forced
        // refreshAll will supply correct data, so we drop the result here.
        let currentlyLive = context.id == (self.store.liveProfileId ?? "")
        if context.isLive != currentlyLive { return }

        // Re-check whether auth was torn down while we fetched. Live profiles
        // use a cheap main-safe file stat; non-live profiles re-query the
        // vault, but off the main actor so a Keychain consent prompt cannot
        // block the menu bar.
        let stillAvailable = currentlyLive
            ? self.store.liveAuthExists()
            : await Self.loadAuthAvailability(source: source, profileID: context.id)
        guard !Task.isCancelled, self.refreshGeneration == generation else { return }
        if !stillAvailable {
            let overrideDecision = context.cached != nil ? "relogin-needed" : "not-set-up"
            var missingDiagnostics = diagnostics
            missingDiagnostics.lastDecision = overrideDecision
            self.store.updateRefreshDiagnostics(context.id, missingDiagnostics)
            self.store.updateStatus(context.id, context.cached.map { .reloginNeeded($0) } ?? .notSetUp)
            self.onChange?()
            return
        }
        self.store.updateRefreshDiagnostics(context.id, diagnostics)
        self.store.updateStatus(context.id, status)
        self.onChange?()
    }

    /// Releases a lease claimed by `refreshGroup` and clears it from
    /// `activeLeases`. Called explicitly at every exit point of
    /// `refreshGroup` after the lease is claimed, since `removeLease` is
    /// `async` and `defer` bodies cannot contain `await`.
    private func releaseLease(_ profileIDs: [String], expectedToken: String) async {
        await Self.removeLease(profileIDs: profileIDs, expectedToken: expectedToken, paths: self.paths)
        for id in profileIDs where self.activeLeases[id] == expectedToken {
            self.activeLeases.removeValue(forKey: id)
        }
    }

    /// Thrown inside the lock closures below to short-circuit a write when
    /// `loadCache` reports `.decodeFailed` — the on-disk cache is
    /// unknown-but-real and must not be clobbered with an empty/partial one.
    private struct CacheDecodeFailed: Error {}

    private nonisolated static func claimLease(
        profileIDs: [String], reservation: LeaseReservation, paths: AppPaths
    ) async -> Bool {
        do {
            try CacheLock.withLock(at: paths.cacheLockURL) {
                var cache: UsageCache
                switch Self.loadCache(paths: paths) {
                case .missing:
                    cache = UsageCache(snapshots: [:])
                case .loaded(let loaded):
                    cache = loaded
                case .decodeFailed:
                    AppLogger.warning(
                        "Usage cache failed to decode; refusing to claim lease over unknown on-disk state",
                        metadata: ["path": paths.cacheURL.path])
                    throw CacheDecodeFailed()
                }
                for profileID in profileIDs {
                    if let existing = cache.leases[profileID], existing.token != reservation.token {
                        guard existing.isActive() || !existing.home.isEmpty else {
                            cache.leases.removeValue(forKey: profileID)
                            continue
                        }
                        throw LeaseClaimLost()
                    }
                }
                for profileID in profileIDs {
                    cache.leases[profileID] = reservation
                }
                try Self.saveCache(cache, paths: paths)
            }
            return true
        } catch {
            return false
        }
    }

    private nonisolated static func removeLease(
        profileIDs: [String], expectedToken: String, paths: AppPaths
    ) async {
        try? CacheLock.withLock(at: paths.cacheLockURL) {
            var cache: UsageCache
            switch Self.loadCache(paths: paths) {
            case .missing:
                cache = UsageCache(snapshots: [:])
            case .loaded(let loaded):
                cache = loaded
            case .decodeFailed:
                AppLogger.warning(
                    "Usage cache failed to decode; refusing to release lease over unknown on-disk state",
                    metadata: ["path": paths.cacheURL.path])
                return
            }
            for profileID in profileIDs where cache.leases[profileID]?.token == expectedToken {
                cache.leases.removeValue(forKey: profileID)
            }
            try Self.saveCache(cache, paths: paths)
        }
    }

    /// Outcome of attempting to load the on-disk cache. Distinguishes "no file
    /// yet" (an empty cache is the correct value, safe to write back) from
    /// "a file exists but didn't decode" (its content is unknown-but-real —
    /// missing, unreadable, corrupt, or containing a sub-record shape this
    /// build can't parse — and callers must not overwrite it).
    private enum CacheLoadResult {
        case missing
        case decodeFailed
        case loaded(UsageCache)
    }

    private nonisolated static func loadCache(paths: AppPaths) -> CacheLoadResult {
        guard FileManager.default.fileExists(atPath: paths.cacheURL.path) else {
            return .missing
        }
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601
        guard let data = try? Data(contentsOf: paths.cacheURL),
              let cache = try? decoder.decode(UsageCache.self, from: data) else {
            return .decodeFailed
        }
        return .loaded(cache)
    }

    private nonisolated static func saveCache(_ cache: UsageCache, paths: AppPaths) throws {
        let encoder = JSONEncoder()
        encoder.dateEncodingStrategy = .iso8601
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try AtomicFileWriter.write(try encoder.encode(cache), to: paths.cacheURL)
    }

    private struct LeaseClaimLost: Error {}

    /// Loads the auth blob off the main actor. `nonisolated` so that awaiting it
    /// from the main actor runs the body on the global concurrent executor,
    /// keeping the potentially-blocking file/Keychain syscall off the main thread.
    private nonisolated static func loadAuthData(
        source: ProfileStore.UsageAuthSource, profileID: String, isLive: Bool
    ) async throws -> Data? {
        if isLive {
            guard FileManager.default.fileExists(atPath: source.liveAuthURL.path) else { return nil }
            return try Data(contentsOf: source.liveAuthURL)
        }
        return try source.vault.loadAuthBlob(profileID: profileID)
    }

    /// Checks a non-live profile's auth-blob availability off the main actor,
    /// mirroring the threading of `loadAuthData` so a Keychain consent prompt
    /// cannot block the main thread.
    private nonisolated static func loadAuthAvailability(
        source: ProfileStore.UsageAuthSource, profileID: String
    ) async -> Bool {
        ((try? source.vault.authBlobAvailability(profileID: profileID)) ?? .missing) == .present
    }
}
