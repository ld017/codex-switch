import AppKit

@main
enum CodexProviderSwitcherMain {
    @MainActor
    static func main() {
        let application = NSApplication.shared
        let arguments = LaunchArguments(arguments: CommandLine.arguments)
        let delegate = AppDelegate(
            enableLoginItemOnLaunch: arguments.enableLoginItem
        )
        application.delegate = delegate
        application.run()
    }
}
