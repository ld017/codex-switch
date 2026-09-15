import Foundation

public struct AppServerLineBuffer {
    private var pending = Data()

    public init() {}

    public mutating func append(_ data: Data) -> [Data] {
        pending.append(data)
        var lines: [Data] = []
        var lineStart = pending.startIndex

        while lineStart < pending.endIndex,
              let newlineIndex = pending[lineStart...].firstIndex(of: 0x0A) {
            var line = Data(pending[lineStart..<newlineIndex])
            if line.last == 0x0D {
                line.removeLast()
            }
            lines.append(line)
            lineStart = pending.index(after: newlineIndex)
        }

        if lineStart != pending.startIndex {
            pending.removeSubrange(pending.startIndex..<lineStart)
        }

        return lines
    }

    public mutating func finish() -> Data? {
        guard !pending.isEmpty else {
            return nil
        }
        defer { pending.removeAll(keepingCapacity: true) }
        return pending
    }
}

public enum AppServerRequestRewriter {
    public static func rewrite(line: String) -> String {
        guard let data = line.data(using: .utf8),
              var request = try? JSONSerialization.jsonObject(with: data)
                as? [String: Any],
              request["method"] as? String == "thread/list",
              var params = request["params"] as? [String: Any]
        else {
            return line
        }

        if let modelProviders = params["modelProviders"],
           !(modelProviders is NSNull) {
            return line
        }

        params["modelProviders"] = []
        request["params"] = params

        guard let rewrittenData = try? JSONSerialization.data(
            withJSONObject: request,
            options: .withoutEscapingSlashes
        ), let rewrittenLine = String(data: rewrittenData, encoding: .utf8) else {
            return line
        }
        return rewrittenLine
    }
}
