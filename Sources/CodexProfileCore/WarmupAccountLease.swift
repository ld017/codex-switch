import Foundation

/// Cooperates with the existing helper/renewal cache. Never overwrites an
/// unreadable cache and never holds its file lock while a child is running.
public struct WarmupAccountLease: Sendable {
    public let token: String
    public let profileIDs: [String]
    private let cacheURL: URL
    private let lockURL: URL
    private let temporaryRoot: URL

    public static func acquire(profileIDs: [String], paths: AppPaths, now: Date = Date()) throws -> Self {
        let token = UUID().uuidString
        let ids = Array(Set(profileIDs)).sorted()
        guard !ids.isEmpty else { throw AuthError.notFound }
        try Self.rejectLinks([paths.cacheURL, paths.cacheLockURL])
        try CacheLock.withLock(at: paths.cacheLockURL) {
            var cache = try load(paths)
            for id in ids {
                if let existing = cache.leases[id], existing.isActive(now: now) || !existing.home.isEmpty {
                    throw CodexRPCError.requestFailed("账号正在被刷新、续期或其他任务使用")
                }
            }
            for id in ids {
                cache.leases[id] = LeaseReservation(token: token, home: "", expiresAt: now.addingTimeInterval(300), createdAt: now)
            }
            try save(cache, paths)
        }
        return Self(token: token, profileIDs: ids, cacheURL: paths.cacheURL, lockURL: paths.cacheLockURL, temporaryRoot: paths.tempRoot.appendingPathComponent("warmup"))
    }

    public func quarantine(ownedHome: URL) throws {
        guard ownedHome.standardizedFileURL.deletingLastPathComponent().path == temporaryRoot.standardizedFileURL.path else { throw WarmupOwnerError.pathOutsideRoot }
        try Self.rejectLinks([ownedHome, cacheURL, lockURL])
        try AtomicFileWriter.ensurePrivateDirectory(ownedHome)
        try CacheLock.withLock(at: lockURL) {
            var cache = try Self.load(cacheURL)
            for id in profileIDs where cache.leases[id]?.token == token {
                let existing = cache.leases[id]!
                cache.leases[id] = LeaseReservation(token: existing.token, home: ownedHome.path, expiresAt: .distantFuture, createdAt: existing.createdAt)
            }
            try Self.save(cache, cacheURL)
        }
    }

    public func release() throws {
        try Self.rejectLinks([cacheURL, lockURL])
        try CacheLock.withLock(at: lockURL) {
            var cache = try Self.load(cacheURL)
            for id in profileIDs where cache.leases[id]?.token == token { cache.leases.removeValue(forKey: id) }
            try Self.save(cache, cacheURL)
        }
    }

    private static func load(_ paths: AppPaths) throws -> UsageCache { try load(paths.cacheURL) }
    private static func load(_ url: URL) throws -> UsageCache {
        guard FileManager.default.fileExists(atPath: url.path) else { return UsageCache(snapshots: [:]) }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        return try decoder.decode(UsageCache.self, from: Data(contentsOf: url))
    }
    private static func save(_ cache: UsageCache, _ paths: AppPaths) throws { try save(cache, paths.cacheURL) }
    private static func save(_ cache: UsageCache, _ url: URL) throws {
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        try AtomicFileWriter.write(try encoder.encode(cache), to: url)
    }
    private static func rejectLinks(_ urls: [URL]) throws {
        for url in urls {
            var ancestor = url.standardizedFileURL
            while ancestor.path != "/" {
                if (try? ancestor.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true { throw WarmupOwnerError.symbolicLinkRejected }
                ancestor.deleteLastPathComponent()
            }
        }
    }
}
