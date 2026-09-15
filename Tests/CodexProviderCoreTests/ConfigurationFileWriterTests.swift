import Foundation
import XCTest
@testable import CodexProviderCore

final class ConfigurationFileWriterTests: XCTestCase {
    private var temporaryDirectory: URL!
    private var configURL: URL!

    override func setUpWithError() throws {
        temporaryDirectory = FileManager.default.temporaryDirectory
            .appendingPathComponent(UUID().uuidString, isDirectory: true)
        try FileManager.default.createDirectory(
            at: temporaryDirectory,
            withIntermediateDirectories: true
        )
        configURL = temporaryDirectory.appendingPathComponent("config.toml")
    }

    override func tearDownWithError() throws {
        try? FileManager.default.removeItem(at: temporaryDirectory)
    }

    func testAtomicReplacementPreservesLivePermissions() throws {
        try Data("old".utf8).write(to: configURL)
        try FileManager.default.setAttributes(
            [.posixPermissions: NSNumber(value: 0o640)],
            ofItemAtPath: configURL.path
        )

        try ConfigurationFileWriter.replace(
            contents: Data("new".utf8),
            at: configURL
        )

        XCTAssertEqual(try Data(contentsOf: configURL), Data("new".utf8))
        let attributes = try FileManager.default.attributesOfItem(
            atPath: configURL.path
        )
        XCTAssertEqual(attributes[.posixPermissions] as? NSNumber, 0o640)
        XCTAssertEqual(
            try FileManager.default.contentsOfDirectory(
                atPath: temporaryDirectory.path
            ),
            ["config.toml"]
        )
    }

    func testRejectsSymbolicLinkWithoutChangingTarget() throws {
        let targetURL = temporaryDirectory.appendingPathComponent("real.toml")
        try Data("original".utf8).write(to: targetURL)
        try FileManager.default.createSymbolicLink(
            at: configURL,
            withDestinationURL: targetURL
        )

        XCTAssertThrowsError(
            try ConfigurationFileWriter.replace(
                contents: Data("changed".utf8),
                at: configURL
            )
        ) { error in
            XCTAssertEqual(
                error as? ConfigurationFileWriterError,
                .symbolicLinkUnsupported
            )
        }

        XCTAssertEqual(try Data(contentsOf: targetURL), Data("original".utf8))
        let values = try configURL.resourceValues(
            forKeys: [.isSymbolicLinkKey]
        )
        XCTAssertEqual(values.isSymbolicLink, true)
    }
}
