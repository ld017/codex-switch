import Foundation

public struct AppServerLineBuffer {
    private var pending = Data()
    private var scannedByteCount = 0

    public init() {}

    public mutating func append(_ data: Data) -> [Data] {
        pending.append(data)
        var lines: [Data] = []
        var lineStart = pending.startIndex
        var searchStart = pending.index(lineStart, offsetBy: scannedByteCount)

        while searchStart < pending.endIndex,
              let newlineIndex = pending[searchStart...].firstIndex(of: 0x0A) {
            var line = Data(pending[lineStart..<newlineIndex])
            if line.last == 0x0D {
                line.removeLast()
            }
            lines.append(line)
            lineStart = pending.index(after: newlineIndex)
            searchStart = lineStart
        }

        if lineStart != pending.startIndex {
            pending.removeSubrange(pending.startIndex..<lineStart)
        }
        // The retained partial line has already been scanned. Only new bytes
        // need to be searched on the next append, regardless of its length.
        scannedByteCount = pending.count

        return lines
    }

    public mutating func finish() -> Data? {
        scannedByteCount = 0
        guard !pending.isEmpty else {
            return nil
        }
        defer { pending.removeAll(keepingCapacity: true) }
        return pending
    }
}

public enum AppServerRequestRewriter {
    public static func rewrite(line: String) -> String {
        String(data: rewrite(line: Data(line.utf8)), encoding: .utf8) ?? line
    }

    public static func rewrite(line: Data) -> Data {
        guard var request = try? JSONSerialization.jsonObject(with: line)
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

        // JSONSerialization also accepts UTF-16/32. The original proxy only
        // rewrote data that could be decoded as UTF-8; retain that boundary
        // without allocating an intermediate String.
        guard isValidUTF8(line) else {
            return line
        }

        params["modelProviders"] = []
        request["params"] = params

        guard let rewrittenData = try? JSONSerialization.data(
            withJSONObject: request,
            options: .withoutEscapingSlashes
        ) else {
            return line
        }
        return rewrittenData
    }

    private static func isValidUTF8(_ data: Data) -> Bool {
        data.withUnsafeBytes { (bytes: UnsafeRawBufferPointer) in
            var iterator = bytes.makeIterator()
            var decoder = Unicode.UTF8()
            while true {
                switch decoder.decode(&iterator) {
                case .scalarValue:
                    continue
                case .emptyInput:
                    return true
                case .error:
                    return false
                }
            }
        }
    }
}
