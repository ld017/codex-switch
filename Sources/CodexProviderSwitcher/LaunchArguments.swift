import Foundation

struct LaunchArguments {
    let enableLoginItem: Bool

    init(arguments: [String]) {
        enableLoginItem = arguments.dropFirst().contains(
            "--enable-login-item"
        )
    }
}
