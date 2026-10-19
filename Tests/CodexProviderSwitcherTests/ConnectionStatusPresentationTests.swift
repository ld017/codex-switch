import XCTest
import CodexProviderCore
@testable import CodexProviderSwitcher

final class ConnectionStatusPresentationTests: XCTestCase {
    private let now = Date(timeIntervalSince1970: 1_000)
    private func present(_ snapshot: ConnectionStatusSnapshot) -> ConnectionStatusPresentation {
        .make(snapshot: snapshot, now: now)
    }
    func testUnknownAndUnreadableNeverImplyConnectivity() {
        XCTAssertEqual(present(.init()).title, "连接：读取中…")
        var s = ConnectionStatusSnapshot()
        s.provider = .unreadable
        s.sub2APIOnline = false; s.sub2APICheckedAt = now
        XCTAssertEqual(present(s).title, "连接：配置无法读取")
        XCTAssertEqual(present(s).emphasis, .attention)
    }
    func testOpenAIIsUnaffectedByBackupPortProxyAndSharedFailures() {
        var s = ConnectionStatusSnapshot()
        s.provider = .configured(.openai)
        s.sub2APIOnline = false; s.sub2APICheckedAt = now
        s.clashReadiness = .systemProxyDisabled
        s.sharedHistoryConfigured = false
        XCTAssertEqual(present(s).title, "连接：OpenAI 官方")
        XCTAssertEqual(present(s).emphasis, .normal)
        XCTAssertTrue(present(s).details.contains { $0.title == "本机 Sub2API 端口：检查未通过" })
    }
    func testOnlyFreshCurrentSub2APIFailureWarnsIncludingClockRollback() {
        for (age, warning) in [(0.0, true), (59, true), (60, false), (61, false), (-1, false)] {
            var s = ConnectionStatusSnapshot()
            s.provider = .configured(.sub2api)
            s.sub2APIOnline = false; s.sub2APICheckedAt = now.addingTimeInterval(-age)
            XCTAssertEqual(present(s).title, warning ? "Sub2API · 端口检查未通过" : "连接：Sub2API")
            XCTAssertEqual(present(s).emphasis, warning ? .attention : .normal)
        }
    }
    func testUncheckedCheckingAndSuccessHaveDifferentDetails() {
        var s = ConnectionStatusSnapshot()
        s.provider = .configured(.sub2api)
        XCTAssertEqual(present(s).title, "连接：Sub2API")
        XCTAssertEqual(present(s).details[1].title, "本机 Sub2API 端口：未检查")
        s.isRefreshingActiveChannel = true
        XCTAssertEqual(present(s).details[1].title, "本机 Sub2API 端口：检查中…")
        s.sub2APIOnline = true; s.sub2APICheckedAt = now
        XCTAssertEqual(present(s).details[1].title, "本机 Sub2API 端口：已响应")
        XCTAssertEqual(present(s).title, "连接：Sub2API")
    }
    func testReadinessDescriptionsDescribePreflightOnly() {
        let cases: [(ClashReadinessStatus, String)] = [
            (.ready, "符合当前应用要求"), (.localPortUnavailable, "预期端口检查未通过"),
            (.systemProxyDisabled, "系统代理未匹配预期"), (.tunDisabled, "未检测到预期 TUN")]
        for (status, text) in cases {
            var s = ConnectionStatusSnapshot(); s.clashReadiness = status
            XCTAssertEqual(present(s).details[3].title, "代理预检：" + text)
        }
    }
    func testFixedRowsRiskCountAndCachedValuesDuringRefresh() {
        var s = ConnectionStatusSnapshot()
        XCTAssertEqual(present(s).details.map(\.id), [.provider, .sub2API, .proxyEnvironment, .proxyReadiness, .sharedHistory, .lastChecked])
        s.isRefreshingDiagnostics = true
        XCTAssertEqual(present(s).details[2].title, "代理环境风险：检查中…")
        s.proxyConflictVariables = ["HTTP_PROXY", "ALL_PROXY"]
        s.sharedHistoryConfigured = true; s.diagnosticsCheckedAt = now.addingTimeInterval(-70)
        XCTAssertEqual(present(s).details[2].title, "代理环境风险：发现 2 项已知风险")
        XCTAssertEqual(present(s).details[4].title, "共享启动配置：已匹配")
        XCTAssertTrue(present(s).details[5].title.contains("正在更新…"))
        s.isRefreshingDiagnostics = false
        XCTAssertTrue(present(s).details[5].title.contains("缓存已过期"))
        s.proxyConflictVariables = []
        XCTAssertEqual(present(s).details[2].title, "代理环境风险：未发现已知冲突")
        s.sharedHistoryConfigured = false
        XCTAssertEqual(present(s).details[4].title, "共享启动配置：检查未通过")
    }
}
