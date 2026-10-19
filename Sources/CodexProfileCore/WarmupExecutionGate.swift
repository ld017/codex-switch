import Foundation

public enum WarmupExecutionGate {
    public enum Error: LocalizedError, Sendable {
        case tooEarly, missed
        public var errorDescription: String? {
            switch self {
            case .tooEarly: return "系统时间已改变，尚未到触发时点，本轮未发送"
            case .missed: return "已超出触发时点30秒，本轮未发送"
            }
        }
    }

    /// Re-evaluated immediately before committing a send, after CLI setup.
    public static func validate(trigger: Date?, now: Date = Date()) throws {
        guard let trigger else { return }
        guard now >= trigger else { throw Error.tooEarly }
        guard now.timeIntervalSince(trigger) <= 30 else { throw Error.missed }
    }
}
