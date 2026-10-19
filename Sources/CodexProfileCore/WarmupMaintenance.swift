import Foundation
import Darwin

public struct WarmupTemporaryCleanupReport: Sendable, Equatable {
    public var removed: Int = 0
    public var retainedForManualReview: Int = 0
}

public enum WarmupMaintenance {
    public static func pruneTemporaryHomes(root: URL, protectedRunIDs: Set<String> = [], now: Date = Date()) throws -> WarmupTemporaryCleanupReport {
        try rejectLinkedAncestors(root)
        guard FileManager.default.fileExists(atPath: root.path) else { return WarmupTemporaryCleanupReport() }
        var report = WarmupTemporaryCleanupReport()
        for directory in try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isDirectoryKey, .isSymbolicLinkKey]) {
            guard !protectedRunIDs.contains(directory.lastPathComponent) else { continue }
            let values = try directory.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
            guard values.isDirectory == true, values.isSymbolicLink != true else { continue }
            let marker = directory.appendingPathComponent(".warmup-owner.json")
            guard (try? marker.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) != true,
                  let data = try? Data(contentsOf: marker), data.count <= 4096,
                  let owner = try? JSONDecoder().decode(WarmupProcessOwner.self, from: data),
                  owner.runID == directory.lastPathComponent, owner.processIdentifier > 1,
                  now.timeIntervalSince(owner.startedAt) >= 86400 else { continue }
            guard kill(owner.processIdentifier, 0) != 0 && errno == ESRCH else { continue }
            let contents = try FileManager.default.contentsOfDirectory(atPath: directory.path)
            guard contents == [".warmup-owner.json"] else { report.retainedForManualReview += 1; continue }
            try rejectLinkedAncestors(directory)
            // The removal routine rechecks the owner marker and path again.
            let owned = WarmupOwnedTemporaryDirectory(root: root, url: directory, owner: owner)
            try owned.removeIfOwned()
            report.removed += 1
        }
        return report
    }

    /// Only UUID-named regular files in our own results directory are eligible.
    /// Protected active results and control files can never be evicted here.
    public static func pruneResults(root: URL, protectedFiles: Set<String> = [], maxCount: Int = 200,
                                    maxBytes: Int = 4 * 1024 * 1024, maxAge: TimeInterval = 30 * 86400,
                                    now: Date = Date()) throws -> Int {
        let directory = root.appendingPathComponent("results")
        try rejectLinkedAncestors(directory)
        guard FileManager.default.fileExists(atPath: directory.path) else { return 0 }
        let urls = try FileManager.default.contentsOfDirectory(at: directory,
            includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey])
        var candidates: [(URL, Int, Date)] = []
        for url in urls where url.pathExtension == "json" && UUID(uuidString: url.deletingPathExtension().lastPathComponent) != nil {
            let values = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey, .fileSizeKey, .contentModificationDateKey])
            guard values.isRegularFile == true, values.isSymbolicLink != true else { continue }
            candidates.append((url, values.fileSize ?? 0, values.contentModificationDate ?? .distantPast))
        }
        candidates.sort { $0.2 < $1.2 }
        var bytes = candidates.reduce(0) { $0 + $1.1 }, count = candidates.count, removed = 0
        for (url, size, date) in candidates where !protectedFiles.contains(url.lastPathComponent) {
            guard count > maxCount || bytes > maxBytes || now.timeIntervalSince(date) > maxAge else { continue }
            // Do not trust the directory or file metadata collected before the
            // sweep. A renamed directory or replaced entry must not redirect a
            // later deletion outside this owned results directory.
            try rejectLinkedAncestors(directory)
            let current = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard current.isRegularFile == true, current.isSymbolicLink != true else { continue }
            try FileManager.default.removeItem(at: url)
            count -= 1; bytes -= size; removed += 1
        }
        return removed
    }

    private static func rejectLinkedAncestors(_ directory: URL) throws {
        var ancestor = directory
        while ancestor.path != "/" {
            if (try? ancestor.resourceValues(forKeys: [.isSymbolicLinkKey]).isSymbolicLink) == true {
                throw WarmupOwnerError.symbolicLinkRejected
            }
            ancestor.deleteLastPathComponent()
        }
    }
}
