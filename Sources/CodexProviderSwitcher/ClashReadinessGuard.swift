import CodexProviderCore
import Foundation

protocol ClashReadinessSnapshotting {
    func localProxyPortIsOpen() -> Bool
    func systemProxyDescription() -> String
    func interfaceDescription() -> String
}

struct LocalClashReadinessSnapshot: ClashReadinessSnapshotting {
    func localProxyPortIsOpen() -> Bool {
        let result = try? ProcessRunner.run(
            executable: URL(fileURLWithPath: "/usr/bin/nc"),
            arguments: ["-z", "-w", "2", "127.0.0.1", "7897"],
            captureOutput: false,
            timeout: 3
        )
        return result?.exitCode == 0
    }

    func systemProxyDescription() -> String {
        (try? ProcessRunner.run(
            executable: URL(fileURLWithPath: "/usr/sbin/scutil"),
            arguments: ["--proxy"],
            captureOutput: true
        ).standardOutput) ?? ""
    }

    func interfaceDescription() -> String {
        (try? ProcessRunner.run(
            executable: URL(fileURLWithPath: "/sbin/ifconfig"),
            arguments: [],
            captureOutput: true
        ).standardOutput) ?? ""
    }
}

enum ClashReadinessStatus: Equatable {
    case ready
    case localPortUnavailable
    case systemProxyDisabled
    case tunDisabled

    var userDescription: String {
        switch self {
        case .ready:
            return "就绪"
        case .localPortUnavailable:
            return "Clash 本机端口未响应"
        case .systemProxyDisabled:
            return "系统代理未开启"
        case .tunDisabled:
            return "虚拟网卡未开启"
        }
    }
}

struct ClashReadinessGuard {
    let snapshot: any ClashReadinessSnapshotting

    init(
        snapshot: any ClashReadinessSnapshotting =
            LocalClashReadinessSnapshot()
    ) {
        self.snapshot = snapshot
    }

    func status() -> ClashReadinessStatus {
        guard snapshot.localProxyPortIsOpen() else {
            return .localPortUnavailable
        }

        let systemProxy = snapshot.systemProxyDescription()
        guard systemProxy.contains("HTTPEnable : 1"),
              systemProxy.contains("HTTPProxy : 127.0.0.1"),
              systemProxy.contains("HTTPPort : 7897"),
              systemProxy.contains("HTTPSEnable : 1"),
              systemProxy.contains("HTTPSProxy : 127.0.0.1"),
              systemProxy.contains("HTTPSPort : 7897") else {
            return .systemProxyDisabled
        }

        guard snapshot.interfaceDescription().contains("inet 198.18.")
        else {
            return .tunDisabled
        }
        return .ready
    }
}
