import Foundation
import XCTest
@testable import CodexProviderCore

final class BackupManagerTests: XCTestCase {
    private var temporaryDirectory: URL!
    private var configURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true
        )
        configURL = temporaryDirectory.appendingPathComponent("config.toml")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporaryDirectory)
    }

    func testCreatesPrivateBackupAndRestoresIt() throws {
        let backupDirectory = temporaryDirectory
            .appendingPathComponent("backups", isDirectory: true)
        let manager = BackupManager(
            backupDirectory: backupDirectory,
            retentionLimit: 10
        )
        try "original".write(to: configURL, atomically: true, encoding: .utf8)

        let backupURL = try manager.createBackup(of: configURL)
        try "changed".write(to: configURL, atomically: true, encoding: .utf8)
        try manager.restoreBackup(backupURL, to: configURL)

        XCTAssertEqual(try String(contentsOf: configURL), "original")
        let attributes = try FileManager.default.attributesOfItem(
            atPath: backupURL.path
        )
        XCTAssertEqual(attributes[.posixPermissions] as? NSNumber, 0o600)
    }

    func testRestorePreservesLiveTargetPermissions() throws {
        let backupDirectory = temporaryDirectory
            .appendingPathComponent("backups", isDirectory: true)
        let manager = BackupManager(
            backupDirectory: backupDirectory,
            retentionLimit: 10
        )
        try "original".write(to: configURL, atomically: true, encoding: .utf8)
        let backupURL = try manager.createBackup(of: configURL)
        try "changed".write(to: configURL, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o640)],
            ofItemAtPath: configURL.path
        )

        try manager.restoreBackup(backupURL, to: configURL)

        let attributes = try FileManager.default.attributesOfItem(
            atPath: configURL.path
        )
        XCTAssertEqual(attributes[.posixPermissions] as? NSNumber, 0o640)
        XCTAssertEqual(try String(contentsOf: configURL), "original")
    }

    func testRetentionKeepsNewestTen() throws {
        let backupDirectory = temporaryDirectory
            .appendingPathComponent("backups", isDirectory: true)
        let manager = BackupManager(
            backupDirectory: backupDirectory,
            retentionLimit: 10
        )

        for index in 0..<12 {
            try "config \(index)".write(
                to: configURL,
                atomically: true,
                encoding: .utf8
            )
            _ = try manager.createBackup(of: configURL)
        }

        XCTAssertEqual(try manager.backupURLs().count, 10)
    }
}
