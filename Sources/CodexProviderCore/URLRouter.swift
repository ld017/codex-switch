import Foundation

public enum SwitcherURLAction: Equatable {
    case switchProvider(Provider)
    case refreshSharedHistory
}

public enum URLRouter {
    public static func action(from url: URL) -> SwitcherURLAction? {
        guard url.scheme == "codex-switcher",
              url.user == nil,
              url.password == nil,
              url.port == nil,
              url.query == nil,
              url.fragment == nil else {
            return nil
        }
        let path = URLComponents(
            url: url,
            resolvingAgainstBaseURL: false
        )?.percentEncodedPath
        switch (url.host, path) {
        case ("switch", "/openai"):
            return .switchProvider(.openai)
        case ("switch", "/sub2api"):
            return .switchProvider(.sub2api)
        case ("shared-history", "/refresh"):
            return .refreshSharedHistory
        default:
            return nil
        }
    }
}
