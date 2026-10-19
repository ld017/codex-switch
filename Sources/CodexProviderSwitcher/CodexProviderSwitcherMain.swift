import AppKit
#if DEBUG
import CodexProfileCore
#endif

@main
enum CodexProviderSwitcherMain {
    @MainActor
    static func main() {
        let application = NSApplication.shared
#if DEBUG
        if CommandLine.arguments.contains("--warmup-ui-preview") || ProcessInfo.processInfo.environment["CODEX_WARMUP_UI_PREVIEW"] == "1" {
            // UI acceptance uses the production window and scheduler with an
            // isolated home, and never loads real accounts or desktop config.
            let model = WarmupViewModel()
            let home = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent(".codex-switcher/tmp/ui-qa-" + UUID().uuidString)
            let paths = AppPaths(environment: ["CODEX_PROFILE_HOME": home.path])
            let profiles = (1...4).map { WarmupProfileItem(id: "fixture-\($0)", label: "测试账号 \($0)", isAvailable: true) }
            let service = WarmupService(viewModel: model, paths: paths, profiles: { profiles }, auth: { _ in throw AuthError.notFound })
            let controller = WarmupWindowController(viewModel: model)
            application.setActivationPolicy(.regular)
            service.start(); controller.show()
            withExtendedLifetime((service, controller)) { application.run() }
            return
        }
#endif
        let arguments = LaunchArguments(arguments: CommandLine.arguments)
        let delegate = AppDelegate(
            enableLoginItemOnLaunch: arguments.enableLoginItem
        )
        application.delegate = delegate
        application.run()
    }
}
