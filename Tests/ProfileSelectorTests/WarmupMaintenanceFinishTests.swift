import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup conservative cleanup and leases")
struct WarmupMaintenanceFinishTests {
    private func root() -> URL {
        URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-finish-" + UUID().uuidString)
    }

    @Test("only stale owner-only homes with dead parents are removed")
    func conservativeTemporaryCleanup() throws {
        let root = root()
        defer { try? FileManager.default.removeItem(at: root) }
        let now = Date()
        let old = now.addingTimeInterval(-2 * 86400)
        let dead: Int32 = 2_147_483_647
        let empty = try WarmupOwnedTemporaryDirectory.create(under: root, owner: .init(runID: "empty", processIdentifier: dead, startedAt: old))
        let content = try WarmupOwnedTemporaryDirectory.create(under: root, owner: .init(runID: "content", processIdentifier: dead, startedAt: old))
        try Data("auth-or-cli-file".utf8).write(to: content.url.appendingPathComponent("auth.json"))
        let active = try WarmupOwnedTemporaryDirectory.create(under: root, owner: .init(runID: "active", processIdentifier: ProcessInfo.processInfo.processIdentifier, startedAt: old))
        let protected = try WarmupOwnedTemporaryDirectory.create(under: root, owner: .init(runID: "protected", processIdentifier: dead, startedAt: old))
        let fresh = try WarmupOwnedTemporaryDirectory.create(under: root, owner: .init(runID: "fresh", processIdentifier: dead, startedAt: now))
        let report = try WarmupMaintenance.pruneTemporaryHomes(root: root, protectedRunIDs: ["protected"], now: now)
        #expect(report.removed == 1)
        #expect(report.retainedForManualReview == 1)
        #expect(!FileManager.default.fileExists(atPath: empty.url.path))
        for url in [content.url, active.url, protected.url, fresh.url] {
            #expect(FileManager.default.fileExists(atPath: url.path))
        }
    }

    @Test("temporary cleanup rejects a linked ancestor and preserves foreign data")
    func linkedTemporaryAncestor() throws {
        let base = root()
        defer { try? FileManager.default.removeItem(at: base) }
        let outside = base.appendingPathComponent("outside")
        try FileManager.default.createDirectory(at: outside, withIntermediateDirectories: true)
        let foreign = outside.appendingPathComponent("user.txt")
        try Data("keep".utf8).write(to: foreign)
        let link = base.appendingPathComponent("link")
        try FileManager.default.createSymbolicLink(at: link, withDestinationURL: outside)
        #expect(throws: WarmupOwnerError.self) { _ = try WarmupMaintenance.pruneTemporaryHomes(root: link.appendingPathComponent("warmup")) }
        #expect(try String(contentsOf: foreign, encoding: .utf8) == "keep")
    }

    @Test("lease rejects a linked lock before open creates its target")
    func linkedLeaseLock() throws {
        let base = root()
        defer { try? FileManager.default.removeItem(at: base) }
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": base.path])
        try FileManager.default.createDirectory(at: paths.switcherHome, withIntermediateDirectories: true)
        let target = base.appendingPathComponent("must-not-create")
        try FileManager.default.createSymbolicLink(at: paths.cacheLockURL, withDestinationURL: target)
        #expect(throws: WarmupOwnerError.self) { _ = try WarmupAccountLease.acquire(profileIDs: ["one"], paths: paths) }
        #expect(!FileManager.default.fileExists(atPath: target.path))
    }

    @Test("quarantine remains reserved after normal expiry and never changes another owner's lease")
    func quarantineReservation() throws {
        let base = root()
        defer { try? FileManager.default.removeItem(at: base) }
        let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": base.path])
        let now = Date()
        let lease = try WarmupAccountLease.acquire(profileIDs: ["one", "alias"], paths: paths, now: now)
        let home = paths.tempRoot.appendingPathComponent("warmup/blocked")
        try FileManager.default.createDirectory(at: home, withIntermediateDirectories: true)
        try lease.quarantine(ownedHome: home)
        #expect(throws: (any Error).self) {
            _ = try WarmupAccountLease.acquire(profileIDs: ["one"], paths: paths, now: now.addingTimeInterval(7 * 86400))
        }
        let decoder = JSONDecoder(); decoder.dateDecodingStrategy = .iso8601
        var cache = try decoder.decode(UsageCache.self, from: Data(contentsOf: paths.cacheURL))
        #expect(cache.leases["alias"]?.home == home.path)
        cache.leases["one"] = .init(token: "new-owner", home: "", expiresAt: now.addingTimeInterval(100), createdAt: now)
        let encoder = JSONEncoder(); encoder.dateEncodingStrategy = .iso8601
        try AtomicFileWriter.write(try encoder.encode(cache), to: paths.cacheURL)
        try lease.quarantine(ownedHome: home)
        cache = try decoder.decode(UsageCache.self, from: Data(contentsOf: paths.cacheURL))
        #expect(cache.leases["one"]?.token == "new-owner")
        #expect(cache.leases["one"]?.home == "")
    }
}
