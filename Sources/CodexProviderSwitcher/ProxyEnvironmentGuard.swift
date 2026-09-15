import CodexProviderCore
import Foundation

protocol ProxyEnvironmentReading {
    func value(for variable: String) -> String?
}

struct LaunchctlProxyEnvironmentReader: ProxyEnvironmentReading {
    func value(for variable: String) -> String? {
        guard let result = try? ProcessRunner.run(
            executable: URL(fileURLWithPath: "/bin/launchctl"),
            arguments: ["getenv", variable],
            captureOutput: true
        ), result.exitCode == 0 else {
            return nil
        }
        let value = result.standardOutput.trimmingCharacters(
            in: .whitespacesAndNewlines
        )
        return value.isEmpty ? nil : value
    }
}

struct ProxyEnvironmentConflict: Equatable, LocalizedError {
    let variable: String

    var errorDescription: String? {
        "\(variable) 指向 Apple Container 桥接代理，可能造成 Clash TUN 自循环。"
    }
}

struct ProxyEnvironmentGuard {
    static let variables = [
        "HTTP_PROXY",
        "HTTPS_PROXY",
        "ALL_PROXY",
        "http_proxy",
        "https_proxy",
        "all_proxy",
    ]

    let reader: any ProxyEnvironmentReading

    init(
        reader: any ProxyEnvironmentReading =
            LaunchctlProxyEnvironmentReader()
    ) {
        self.reader = reader
    }

    func conflicts() -> [ProxyEnvironmentConflict] {
        Self.variables.compactMap { variable in
            guard let value = reader.value(for: variable),
                  proxyHost(from: value) == "192.168.65.1" else {
                return nil
            }
            return ProxyEnvironmentConflict(variable: variable)
        }
    }

    private func proxyHost(from value: String) -> String? {
        if let host = URLComponents(string: value)?.host {
            return host.lowercased()
        }
        return URLComponents(string: "//\(value)")?.host?.lowercased()
    }
}
