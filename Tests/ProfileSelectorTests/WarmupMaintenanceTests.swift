import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warmup owned storage maintenance")
struct WarmupMaintenanceTests {
    @Test("history pruning is bounded and never removes the control file or an active result")
    func boundedHistory() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        let results = root.appendingPathComponent("results")
        try FileManager.default.createDirectory(at: results, withIntermediateDirectories: true)
        try Data("keep".utf8).write(to: root.appendingPathComponent("control.json"))
        let a = results.appendingPathComponent(UUID().uuidString + ".json")
        let b = results.appendingPathComponent(UUID().uuidString + ".json")
        let c = results.appendingPathComponent(UUID().uuidString + ".json")
        for (n,p) in [a,b,c].enumerated() {
            try Data(repeating: 65, count: 10).write(to: p)
            try FileManager.default.setAttributes([.modificationDate: Date(timeIntervalSince1970: Double(n + 1))], ofItemAtPath: p.path)
        }
        _ = try WarmupMaintenance.pruneResults(root: root, protectedFiles: [c.lastPathComponent], maxCount: 1, maxBytes: 100, maxAge: .greatestFiniteMagnitude)
        #expect(FileManager.default.fileExists(atPath: c.path))
        #expect(!FileManager.default.fileExists(atPath: a.path))
        #expect(FileManager.default.fileExists(atPath: root.appendingPathComponent("control.json").path))
    }

    @Test("foreign files and symlinks are never swept as warmup results")
    func refusesSymlink() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: root) }
        try FileManager.default.createDirectory(at: root.appendingPathComponent("results"), withIntermediateDirectories: true)
        let target = root.appendingPathComponent("user-file.txt")
        try Data("user".utf8).write(to: target)
        try FileManager.default.createSymbolicLink(at: root.appendingPathComponent("results/" + UUID().uuidString + ".json"), withDestinationURL: target)
        _ = try WarmupMaintenance.pruneResults(root: root, maxCount: 0, maxBytes: 0)
        #expect(try String(contentsOf: target, encoding: .utf8) == "user")
    }
}
