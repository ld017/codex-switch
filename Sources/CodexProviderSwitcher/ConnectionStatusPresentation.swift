import CodexProviderCore
import CodexProfileCore
import Foundation

enum MenuProviderState: Equatable {
    case checking
    case configured(Provider)
    case unreadable
}

struct ConnectionStatusSnapshot: Equatable {
    static let cacheLifetime: TimeInterval = 60
    var provider: MenuProviderState = .checking
    var sub2APIOnline: Bool?
    var sub2APICheckedAt: Date?
    var proxyConflictVariables: [String]?
    var clashReadiness: ClashReadinessStatus?
    var sharedHistoryConfigured: Bool?
    var diagnosticsCheckedAt: Date?
    var isRefreshingDiagnostics = false
    var isRefreshingActiveChannel = false

    static func isFresh(_ date: Date?, now: Date) -> Bool {
        guard let date else { return false }
        let age = now.timeIntervalSince(date)
        return age >= 0 && age < cacheLifetime
    }
}

struct ConnectionStatusPresentation: Equatable {
    enum Emphasis { case normal, attention }
    struct DetailRow: Equatable {
        enum ID: CaseIterable { case provider, sub2API, proxyEnvironment, proxyReadiness, sharedHistory, lastChecked }
        let id: ID
        let title: String
    }
    let title: String
    let emphasis: Emphasis
    let details: [DetailRow]

    static func make(snapshot s: ConnectionStatusSnapshot, now: Date) -> Self {
        let title: String
        let emphasis: Emphasis
        let provider: String
        switch s.provider {
        case .checking:
            title = "连接：读取中…"; emphasis = .normal; provider = "读取中…"
        case .unreadable:
            title = "连接：配置无法读取"; emphasis = .attention; provider = "配置无法读取"
        case .configured(let configured):
            provider = configured.displayName
            let failed = configured == .sub2api && s.sub2APIOnline == false
                && ConnectionStatusSnapshot.isFresh(s.sub2APICheckedAt, now: now)
            title = failed ? "Sub2API · 端口检查未通过" : "连接：" + provider
            emphasis = failed ? .attention : .normal
        }
        let pending = s.isRefreshingDiagnostics ? "检查中…" : "未检查"
        let port = s.sub2APIOnline.map { online in
            let result = online ? "已响应" : "检查未通过"
            return result + (ConnectionStatusSnapshot.isFresh(s.sub2APICheckedAt, now: now)
                ? "" : "（缓存已过期）")
        } ?? (s.isRefreshingActiveChannel || s.isRefreshingDiagnostics ? "检查中…" : "未检查")
        let environment = s.proxyConflictVariables.map {
            $0.isEmpty ? "未发现已知冲突" : "发现 \($0.count) 项已知风险"
        } ?? pending
        let readiness: String
        switch s.clashReadiness {
        case .ready: readiness = "符合当前应用要求"
        case .localPortUnavailable: readiness = "预期端口检查未通过"
        case .systemProxyDisabled: readiness = "系统代理未匹配预期"
        case .tunDisabled: readiness = "未检测到预期 TUN"
        case nil: readiness = pending
        }
        let shared = s.sharedHistoryConfigured.map { $0 ? "已匹配" : "检查未通过" } ?? pending
        let updated: String
        if let date = s.diagnosticsCheckedAt {
            let time = BeijingTime.format(date, pattern: "M月d日 HH:mm:ss'（北京时间）'")
            if s.isRefreshingDiagnostics {
                updated = "正在更新…（上次 \(time)）"
            } else {
                updated = time + (ConnectionStatusSnapshot.isFresh(date, now: now) ? "" : "（缓存已过期）")
            }
        } else {
            updated = pending
        }
        return Self(title: title, emphasis: emphasis, details: [
            .init(id: .provider, title: "配置提供方：" + provider),
            .init(id: .sub2API, title: "本机 Sub2API 端口：" + port),
            .init(id: .proxyEnvironment, title: "代理环境风险：" + environment),
            .init(id: .proxyReadiness, title: "代理预检：" + readiness),
            .init(id: .sharedHistory, title: "共享启动配置：" + shared),
            .init(id: .lastChecked, title: "诊断更新时间：" + updated),
        ])
    }
}
