import Foundation

enum SwitchFailureStage: String, Equatable {
    case proxyCheck = "代理检查"
    case sub2APIRecovery = "Sub2API 恢复"
    case targetValidation = "目标配置检查"
    case sharedHistory = "共享历史准备"
    case codexTermination = "Codex 退出"
    case configuration = "配置写入与验证"
    case codexLaunch = "Codex 重新启动"
    case recovery = "配置恢复"
}

struct StagedSwitchError: LocalizedError {
    let stage: SwitchFailureStage
    let underlying: Error

    var errorDescription: String? {
        "\(stage.rawValue)：\(underlying.localizedDescription)"
    }

    static func wrapping(
        _ error: Error,
        stage: SwitchFailureStage
    ) -> StagedSwitchError {
        if let staged = error as? StagedSwitchError {
            return staged
        }
        return StagedSwitchError(stage: stage, underlying: error)
    }
}

protocol SwitchDiagnosticLogging {
    func record(_ error: StagedSwitchError)
}

final class SwitchDiagnosticLogger: SwitchDiagnosticLogging {
    private let logURL: URL
    private let maximumBytes: Int
    private let fileManager: FileManager
    private let lock = NSLock()

    init(
        logURL: URL? = nil,
        maximumBytes: Int = 200_000,
        fileManager: FileManager = .default
    ) {
        self.fileManager = fileManager
        self.logURL = logURL ?? fileManager.homeDirectoryForCurrentUser
            .appendingPathComponent(
                ".codex/provider-switcher/switcher.log"
            )
        self.maximumBytes = max(maximumBytes, 1)
    }

    func record(_ error: StagedSwitchError) {
        lock.lock()
        defer { lock.unlock() }

        let timestamp = ISO8601DateFormatter().string(from: Date())
        let message = Self.redact(error.underlying.localizedDescription)
        let line = "\(timestamp) [\(error.stage.rawValue)] \(message)\n"
        guard let lineData = line.data(using: .utf8) else {
            return
        }

        do {
            try fileManager.createDirectory(
                at: logURL.deletingLastPathComponent(),
                withIntermediateDirectories: true
            )
            var data = (try? Data(contentsOf: logURL)) ?? Data()
            data.append(lineData)
            if data.count > maximumBytes {
                data = Data(data.suffix(maximumBytes))
            }
            try data.write(to: logURL, options: .atomic)
            try fileManager.setAttributes(
                [.posixPermissions: 0o600],
                ofItemAtPath: logURL.path
            )
        } catch {
            // Diagnostics must never replace the original switch error.
        }
    }

    private static func redact(_ input: String) -> String {
        var output = replacing(
            pattern: #"(?i)\bBearer\s+[^\s,;]+"#,
            in: input,
            with: "Bearer [REDACTED]"
        )
        output = replacing(
            pattern: #"(?i)([a-z][a-z0-9+.-]*://)[^/@\s]+@"#,
            in: output,
            with: "$1[REDACTED]@"
        )
        output = replacing(
            pattern:
                #"(?i)\b(password|token|secret|api[_-]?key)\s*[:=]\s*[^\s,;]+"#,
            in: output,
            with: "$1=[REDACTED]"
        )
        return output
    }

    private static func replacing(
        pattern: String,
        in input: String,
        with replacement: String
    ) -> String {
        guard let expression = try? NSRegularExpression(
            pattern: pattern
        ) else {
            return input
        }
        let range = NSRange(input.startIndex..., in: input)
        return expression.stringByReplacingMatches(
            in: input,
            range: range,
            withTemplate: replacement
        )
    }
}
