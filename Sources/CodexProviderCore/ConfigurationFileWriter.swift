import Darwin
import Foundation

public enum ConfigurationFileWriterError: Error, Equatable {
    case targetMissing
    case symbolicLinkUnsupported
    case replacementFailed
}

public enum ConfigurationFileWriter {
    public static func replace(contents: Data, at targetURL: URL) throws {
        var targetInfo = stat()
        let statResult = targetURL.withUnsafeFileSystemRepresentation {
            path in
            guard let path else {
                return Int32(-1)
            }
            return lstat(path, &targetInfo)
        }
        guard statResult == 0 else {
            throw ConfigurationFileWriterError.targetMissing
        }
        guard targetInfo.st_mode & S_IFMT != S_IFLNK else {
            throw ConfigurationFileWriterError.symbolicLinkUnsupported
        }

        let temporaryURL = targetURL.deletingLastPathComponent()
            .appendingPathComponent(
                ".config-provider-switch-\(UUID().uuidString)"
            )
        do {
            try contents.write(to: temporaryURL, options: [.withoutOverwriting])
            try FileManager.default.setAttributes(
                [
                    .posixPermissions: NSNumber(
                        value: targetInfo.st_mode & 0o7777
                    ),
                ],
                ofItemAtPath: temporaryURL.path
            )
            let renameResult = temporaryURL.withUnsafeFileSystemRepresentation {
                sourcePath in
                targetURL.withUnsafeFileSystemRepresentation {
                    targetPath in
                    guard let sourcePath, let targetPath else {
                        return Int32(-1)
                    }
                    return rename(sourcePath, targetPath)
                }
            }
            guard renameResult == 0 else {
                throw ConfigurationFileWriterError.replacementFailed
            }
        } catch {
            try? FileManager.default.removeItem(at: temporaryURL)
            throw error
        }
    }
}
