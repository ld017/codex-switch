import Foundation
import CodexProfileCore

// MARK: - Logging (adapted from CodexBar)

enum LogLevel: String {
    case info = "INFO"
    case warning = "WARN"
    case error = "ERROR"
}

enum AppLogger {
    static let logURL: URL = {
        let base = FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask).first
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library", isDirectory: true)
        return base
            .appendingPathComponent("Logs", isDirectory: true)
            .appendingPathComponent(AppInfo.name, isDirectory: true)
            .appendingPathComponent("\(AppInfo.name).log")
    }()

    private static let queue = DispatchQueue(label: "com.codex-profile-switcher.log", qos: .utility)
    private static let maxBytes: UInt64 = 2 * 1024 * 1024

    static func info(_ message: String, metadata: [String: String] = [:]) {
        self.write(level: .info, message: message, metadata: metadata)
    }

    static func warning(_ message: String, metadata: [String: String] = [:]) {
        self.write(level: .warning, message: message, metadata: metadata)
    }

    static func error(_ message: String, metadata: [String: String] = [:]) {
        self.write(level: .error, message: message, metadata: metadata)
    }

    static func recentLines(limit: Int = 200) -> String {
        guard let data = try? Data(contentsOf: self.logURL),
              let text = String(data: data, encoding: .utf8) else {
            return "<no log file>"
        }
        let lines = text.split(separator: "\n", omittingEmptySubsequences: false).suffix(limit)
        return lines.joined(separator: "\n")
    }

    private static func write(level: LogLevel, message: String, metadata: [String: String]) {
        let safeMessage = LogRedactor.excerpt(message, maxLength: 4096)
        let safeMetadata = metadata
            .sorted { $0.key < $1.key }
            .map { "\($0.key)=\(LogRedactor.excerpt($0.value))" }
            .joined(separator: " ")
        let suffix = safeMetadata.isEmpty ? "" : " \(safeMetadata)"
        let line = "[\(Self.timestamp())] [\(level.rawValue)] \(safeMessage)\(suffix)\n"

        self.queue.async {
            do {
                let data = Data(line.utf8)
                try self.prepareFile(incomingBytes: UInt64(data.count))
                let handle = try FileHandle(forWritingTo: self.logURL)
                defer { try? handle.close() }
                handle.seekToEndOfFile()
                try handle.write(contentsOf: data)
            } catch {
                NSLog("%@: log write failed: %@", AppInfo.name, error.localizedDescription)
            }
        }
    }

    static func prepareFile(at logURL: URL = Self.logURL, now: Date = Date(), incomingBytes: UInt64 = 0) throws {
        let fm = FileManager.default
        let dir = logURL.deletingLastPathComponent()
        try fm.createDirectory(at: dir, withIntermediateDirectories: true,
                               attributes: [.posixPermissions: 0o700])

        let rotated = dir.appendingPathComponent(logURL.deletingPathExtension().lastPathComponent + ".old.log")
        if let attrs = try? fm.attributesOfItem(atPath: rotated.path),
           let date = attrs[.modificationDate] as? Date, now.timeIntervalSince(date) > 14 * 86400 {
            try fm.removeItem(at: rotated)
        }
        if fm.fileExists(atPath: logURL.path) {
            let attrs = try fm.attributesOfItem(atPath: logURL.path)
            let size = (attrs[.size] as? NSNumber)?.uint64Value ?? 0
            if size + min(incomingBytes, self.maxBytes) > self.maxBytes {
                try? fm.removeItem(at: rotated)
                try? fm.moveItem(at: logURL, to: rotated)
            }
        }

        if !fm.fileExists(atPath: logURL.path) {
            fm.createFile(atPath: logURL.path, contents: nil,
                          attributes: [.posixPermissions: 0o600])
        }
    }

    private static let timestampFormatter: ISO8601DateFormatter = {
        let f = ISO8601DateFormatter()
        f.formatOptions = [.withInternetDateTime, .withFractionalSeconds]
        return f
    }()

    private static func timestamp() -> String {
        return self.timestampFormatter.string(from: Date())
    }
}
