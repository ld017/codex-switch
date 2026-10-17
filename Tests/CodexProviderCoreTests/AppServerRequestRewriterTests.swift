import Foundation
import XCTest
@testable import CodexProviderCore

final class AppServerRequestRewriterTests: XCTestCase {
    func testReplacesNullModelProvidersWithAnEmptyArray() throws {
        let output = AppServerRequestRewriter.rewrite(
            line: #"{"method":"thread/list","params":{"modelProviders":null}}"#
        )

        XCTAssertTrue(try XCTUnwrap(modelProviders(in: output) as? [Any]).isEmpty)
    }

    func testAddsAnEmptyModelProvidersArrayWhenTheKeyIsMissing() throws {
        let output = AppServerRequestRewriter.rewrite(
            line: #"{"method":"thread/list","params":{"pageSize":20}}"#
        )

        XCTAssertTrue(try XCTUnwrap(modelProviders(in: output) as? [Any]).isEmpty)
    }

    func testPreservesNonNullExistingFilterByteForByte() {
        let input = #"{ "method" : "thread/list", "params" : { "modelProviders" : ["primary"] } }"#

        XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
    }

    func testPreservesOtherMethodsByteForByte() {
        let input = #"{ "method" : "thread/get", "params" : { "modelProviders" : null } }"#

        XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
    }

    func testPreservesInvalidJSONByteForByte() {
        let input = #"{"method":"thread/list","params":"#

        XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
    }

    func testPreservesRequestsWithoutObjectValuedParamsByteForByte() {
        let input = #"{"method":"thread/list","params":null}"#

        XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
    }

    func testModifiedRequestsDoNotEscapeSlashes() {
        let output = AppServerRequestRewriter.rewrite(
            line: #"{"method":"thread/list","params":{"path":"https://example.invalid/a/b"}}"#
        )

        XCTAssertTrue(output.contains("https://example.invalid/a/b"))
        XCTAssertFalse(output.contains(#"https:\/\/example.invalid\/a\/b"#))
    }

    func testDataInterfaceRewritesEscapedMethodAndParameterKeys() throws {
        let input = Data(#"{"method":"thread\u002flist","par\u0061ms":{"model\u0050roviders":null,"cursor":"中文🙂"}}"#.utf8)
        let output = AppServerRequestRewriter.rewrite(line: input)

        XCTAssertTrue(try XCTUnwrap(modelProviders(in: output) as? [Any]).isEmpty)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: output) as? [String: Any])
        let params = try XCTUnwrap(object["params"] as? [String: Any])
        XCTAssertEqual(params["cursor"] as? String, "中文🙂")
    }

    func testDataInterfacePreservesNonTargetAndInvalidBytesExactly() {
        let inputs = [
            Data(#" { "method" : "thread/get", "params" : { "text" : "thread/list 中文🙂" } } "#.utf8),
            Data(#"{"method":"thread\u002flist","params":{"modelProviders":["primary"]}}"#.utf8),
            Data(#"{"method":"thread/list","params":{"modelProviders":false}}"#.utf8),
            Data(#"{"method":"thread/list","params":{"modelProviders":""}}"#.utf8),
            Data(#"{"method":"thread/list","params":null}"#.utf8),
            Data(#"{"method":"thread/list","params":"#.utf8),
            Data([0xFF, 0xFE, 0x61, 0x00]),
        ]

        for input in inputs {
            XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
        }
    }

    func testDataInterfaceRewritesLargeLegitimateRequestsWithoutTruncation() throws {
        let cursor = String(repeating: "a", count: 2 * 1024 * 1024)
        let input = try JSONSerialization.data(withJSONObject: [
            "method": "thread/list",
            "params": ["cursor": cursor],
        ])
        let output = AppServerRequestRewriter.rewrite(line: input)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: output) as? [String: Any])
        let params = try XCTUnwrap(object["params"] as? [String: Any])

        XCTAssertEqual(params["cursor"] as? String, cursor)
        XCTAssertTrue(try XCTUnwrap(params["modelProviders"] as? [Any]).isEmpty)
    }

    func testDataInterfacePreservesInvalidUTF8EvenWhenFoundationAcceptsTheJSON() throws {
        let input = try XCTUnwrap(
            #"{"method":"thread/list","params":{}}"#.data(using: .utf16)
        )
        XCTAssertNil(String(data: input, encoding: .utf8))
        XCTAssertNoThrow(try JSONSerialization.jsonObject(with: input))

        XCTAssertEqual(AppServerRequestRewriter.rewrite(line: input), input)
    }

    func testDataInterfaceRetainsLegacySupportForUTF8ValidAlternateEncoding() throws {
        let input = try XCTUnwrap(
            #"{"method":"thread/list","params":{}}"#.data(using: .utf16LittleEndian)
        )
        XCTAssertNotNil(String(data: input, encoding: .utf8))

        let output = AppServerRequestRewriter.rewrite(line: input)
        XCTAssertTrue(try XCTUnwrap(modelProviders(in: output) as? [Any]).isEmpty)
    }

    private func modelProviders(in line: String) throws -> Any? {
        try modelProviders(in: XCTUnwrap(line.data(using: .utf8)))
    }

    private func modelProviders(in data: Data) throws -> Any? {
        let object = try JSONSerialization.jsonObject(with: data)
        let request = try XCTUnwrap(object as? [String: Any])
        let params = try XCTUnwrap(request["params"] as? [String: Any])
        return params["modelProviders"]
    }
}
