import Foundation

public enum Provider: String, CaseIterable, Equatable, Sendable {
    case openai
    case sub2api

    public var displayName: String {
        switch self {
        case .openai:
            return "OpenAI 官方"
        case .sub2api:
            return "Sub2API"
        }
    }
}
