import Foundation

public enum ConfigEditorError: LocalizedError, Equatable {
    case duplicateProvider
    case missingProvider
    case missingModelAnchor
    case unknownProvider(String)

    public var errorDescription: String? {
        switch self {
        case .duplicateProvider:
            return "配置中存在多个顶层 model_provider，已停止以避免误改。"
        case .missingProvider:
            return "配置中没有找到顶层 model_provider。"
        case .missingModelAnchor:
            return "配置中没有找到顶层 model，无法安全插入 model_provider。"
        case let .unknownProvider(value):
            return "无法识别当前连接提供方：\(value)"
        }
    }
}

public enum ConfigEditor {
    private static let providerPattern =
        #"^[ \t]*model_provider[ \t]*=[ \t]*"([^"]+)"[ \t]*(?:#.*)?$"#
    private static let providerReplacePattern =
        #"^([ \t]*model_provider[ \t]*=[ \t]*")[^"]+(".*)$"#
    private static let modelPattern = #"^[ \t]*model[ \t]*="#

    public static func currentProvider(in source: String) throws -> Provider {
        let values = try topLevelProviderValues(in: source)
        guard let value = values.first else {
            if hasTopLevelModel(in: source) {
                return .openai
            }
            throw ConfigEditorError.missingProvider
        }
        guard let provider = Provider(rawValue: value) else {
            throw ConfigEditorError.unknownProvider(value)
        }
        return provider
    }

    public static func replacingProvider(
        in source: String,
        with target: Provider
    ) throws -> String {
        var lines = linesPreservingEndings(source)
        var inTopLevel = true
        var providerIndexes: [Int] = []
        var modelIndex: Int?

        for (index, line) in lines.enumerated() {
            let content = removingLineEnding(line)
            let trimmed = content.trimmingCharacters(in: .whitespaces)
            if trimmed.hasPrefix("[") {
                inTopLevel = false
            }
            guard inTopLevel else {
                continue
            }
            if firstMatch(in: content, pattern: providerPattern) != nil {
                providerIndexes.append(index)
            } else if firstMatch(in: content, pattern: modelPattern) != nil {
                modelIndex = index
            }
        }

        if providerIndexes.count > 1 {
            throw ConfigEditorError.duplicateProvider
        }

        if let index = providerIndexes.first {
            let ending = lineEnding(of: lines[index])
            let content = removingLineEnding(lines[index])
            guard let regex = try? NSRegularExpression(
                pattern: providerReplacePattern
            ) else {
                preconditionFailure("Static provider regular expression is invalid")
            }
            let fullRange = NSRange(content.startIndex..., in: content)
            let replacement = regex.stringByReplacingMatches(
                in: content,
                range: fullRange,
                withTemplate: "$1\(target.rawValue)$2"
            )
            lines[index] = replacement + ending
            return lines.joined()
        }

        guard let anchorIndex = modelIndex else {
            throw ConfigEditorError.missingModelAnchor
        }
        var ending = lineEnding(of: lines[anchorIndex])
        if ending.isEmpty {
            ending = preferredLineEnding(in: source)
            lines[anchorIndex] += ending
            lines.insert(
                "model_provider = \"\(target.rawValue)\"",
                at: anchorIndex + 1
            )
        } else {
            lines.insert(
                "model_provider = \"\(target.rawValue)\"\(ending)",
                at: anchorIndex + 1
            )
        }
        return lines.joined()
    }

    public static func hasSub2APIConfiguration(in source: String) -> Bool {
        var hasProvider = false
        var hasAuth = false
        for line in linesPreservingEndings(source) {
            let trimmed = removingLineEnding(line)
                .trimmingCharacters(in: .whitespaces)
            if trimmed == "[model_providers.sub2api]" {
                hasProvider = true
            } else if trimmed == "[model_providers.sub2api.auth]" {
                hasAuth = true
            }
        }
        return hasProvider && hasAuth
    }

    private static func topLevelProviderValues(
        in source: String
    ) throws -> [String] {
        var values: [String] = []
        for line in linesPreservingEndings(source) {
            let content = removingLineEnding(line)
            if content.trimmingCharacters(in: .whitespaces).hasPrefix("[") {
                break
            }
            guard let match = firstMatch(
                in: content,
                pattern: providerPattern
            ), let range = Range(match.range(at: 1), in: content) else {
                continue
            }
            values.append(String(content[range]))
        }
        if values.count > 1 {
            throw ConfigEditorError.duplicateProvider
        }
        return values
    }

    private static func hasTopLevelModel(in source: String) -> Bool {
        for line in linesPreservingEndings(source) {
            let content = removingLineEnding(line)
            if content.trimmingCharacters(in: .whitespaces).hasPrefix("[") {
                return false
            }
            if firstMatch(in: content, pattern: modelPattern) != nil {
                return true
            }
        }
        return false
    }

    private static func firstMatch(
        in value: String,
        pattern: String
    ) -> NSTextCheckingResult? {
        guard let regex = try? NSRegularExpression(pattern: pattern) else {
            return nil
        }
        return regex.firstMatch(
            in: value,
            range: NSRange(value.startIndex..., in: value)
        )
    }

    private static func linesPreservingEndings(_ source: String) -> [String] {
        guard !source.isEmpty else {
            return []
        }
        let value = source as NSString
        var lines: [String] = []
        var start = 0
        while start < value.length {
            var end = start
            while end < value.length {
                let character = value.character(at: end)
                if character == 0x0A {
                    end += 1
                    break
                }
                if character == 0x0D {
                    end += 1
                    if end < value.length, value.character(at: end) == 0x0A {
                        end += 1
                    }
                    break
                }
                end += 1
            }
            lines.append(value.substring(with: NSRange(start..<end)))
            start = end
        }
        return lines
    }

    private static func removingLineEnding(_ line: String) -> String {
        if line.hasSuffix("\r\n") {
            return String(line.dropLast(2))
        }
        if line.hasSuffix("\n") || line.hasSuffix("\r") {
            return String(line.dropLast())
        }
        return line
    }

    private static func lineEnding(of line: String) -> String {
        if line.hasSuffix("\r\n") {
            return "\r\n"
        }
        if line.hasSuffix("\n") {
            return "\n"
        }
        if line.hasSuffix("\r") {
            return "\r"
        }
        return ""
    }

    private static func preferredLineEnding(in source: String) -> String {
        if source.contains("\r\n") {
            return "\r\n"
        }
        if source.contains("\r") {
            return "\r"
        }
        return "\n"
    }
}
