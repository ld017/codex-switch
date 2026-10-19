import Foundation
import XCTest
@testable import CodexProviderSwitcher

final class AppLoggingMaintenanceTests: XCTestCase {
    func testExpiredRotatedLogIsRemovedWithoutChangingCurrentLog() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-logs-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let current = root.appendingPathComponent("app.log"), old = root.appendingPathComponent("app.old.log")
        try Data("current".utf8).write(to: current)
        try Data("old".utf8).write(to: old)
        let now = Date()
        try FileManager.default.setAttributes([.modificationDate: now.addingTimeInterval(-15 * 86400)], ofItemAtPath: old.path)
        try AppLogger.prepareFile(at: current, now: now)
        XCTAssertFalse(FileManager.default.fileExists(atPath: old.path))
        XCTAssertEqual(try String(contentsOf: current, encoding: .utf8), "current")
    }

    func testRotationAccountsForIncomingLineBeforeAppending() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("warmup-logs-" + UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        let current = root.appendingPathComponent("app.log"), old = root.appendingPathComponent("app.old.log")
        let data = Data(repeating: 65, count: 2 * 1024 * 1024 - 10)
        try data.write(to: current)
        try Data("older".utf8).write(to: old)
        try AppLogger.prepareFile(at: current, incomingBytes: 20)
        XCTAssertEqual(try Data(contentsOf: old), data)
        XCTAssertEqual(try Data(contentsOf: current).count, 0)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: root.path).sorted(), ["app.log", "app.old.log"])
    }
}
