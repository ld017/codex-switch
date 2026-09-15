import Foundation

public struct DoctorResult: Sendable {
    public let exitCode: Int32
    public let standardOutput: String

    public init(exitCode: Int32, standardOutput: String) {
        self.exitCode = exitCode
        self.standardOutput = standardOutput
    }

    public var isConfigurationValid: Bool {
        guard let data = standardOutput.data(using: .utf8),
              let root = try? JSONSerialization.jsonObject(with: data)
                as? [String: Any],
              let checks = root["checks"] as? [String: Any],
              let config = checks["config.load"] as? [String: Any],
              let status = config["status"] as? String else {
            return false
        }
        return status == "ok"
    }
}

public enum AuthInspectorError: LocalizedError {
    case missingAuthMode

    public var errorDescription: String? {
        "没有找到 ChatGPT 登录状态。"
    }
}

public enum AuthInspector {
    public static func authMode(from data: Data) throws -> String {
        guard let root = try JSONSerialization.jsonObject(with: data)
                as? [String: Any],
              let mode = root["auth_mode"] as? String,
              !mode.isEmpty else {
            throw AuthInspectorError.missingAuthMode
        }
        return mode
    }
}
