import Foundation

public struct BackupManager: Sendable {
    public let backupDirectory: URL
    public let retentionLimit: Int

    public init(backupDirectory: URL, retentionLimit: Int = 10) {
        self.backupDirectory = backupDirectory
        self.retentionLimit = retentionLimit
    }

    @discardableResult
    public func createBackup(of sourceURL: URL) throws -> URL {
        try FileManager.default.createDirectory(
            at: backupDirectory,
            withIntermediateDirectories: true,
            attributes: [.posixPermissions: NSNumber(value: 0o700)]
        )
        let timestamp = UInt64(Date().timeIntervalSince1970 * 1_000_000)
        let fileName = "config-\(timestamp)-\(UUID().uuidString).toml"
        let backupURL = backupDirectory.appendingPathComponent(fileName)
        try FileManager.default.copyItem(at: sourceURL, to: backupURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o600)],
            ofItemAtPath: backupURL.path
        )
        try prune()
        return backupURL
    }

    public func restoreBackup(_ backupURL: URL, to targetURL: URL) throws {
        let targetPermissions: NSNumber?
        if FileManager.default.fileExists(atPath: targetURL.path) {
            targetPermissions = try FileManager.default.attributesOfItem(
                atPath: targetURL.path
            )[.posixPermissions] as? NSNumber
        } else {
            targetPermissions = nil
        }
        let temporaryURL = targetURL.deletingLastPathComponent()
            .appendingPathComponent(".config-restore-\(UUID().uuidString)")
        try FileManager.default.copyItem(at: backupURL, to: temporaryURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o600)],
            ofItemAtPath: temporaryURL.path
        )
        if FileManager.default.fileExists(atPath: targetURL.path) {
            _ = try FileManager.default.replaceItemAt(
                targetURL,
                withItemAt: temporaryURL,
                backupItemName: nil,
                options: []
            )
        } else {
            try FileManager.default.moveItem(
                at: temporaryURL,
                to: targetURL
            )
        }
        if let targetPermissions {
            try FileManager.default.setAttributes(
                [.posixPermissions: targetPermissions],
                ofItemAtPath: targetURL.path
            )
        }
    }

    public func backupURLs() throws -> [URL] {
        guard FileManager.default.fileExists(
            atPath: backupDirectory.path
        ) else {
            return []
        }
        return try FileManager.default.contentsOfDirectory(
            at: backupDirectory,
            includingPropertiesForKeys: nil,
            options: [.skipsHiddenFiles]
        )
        .filter {
            $0.lastPathComponent.hasPrefix("config-")
                && $0.pathExtension == "toml"
        }
        .sorted {
            $0.lastPathComponent > $1.lastPathComponent
        }
    }

    private func prune() throws {
        let backups = try backupURLs()
        guard backups.count > retentionLimit else {
            return
        }
        for backup in backups.dropFirst(retentionLimit) {
            try FileManager.default.removeItem(at: backup)
        }
    }
}
