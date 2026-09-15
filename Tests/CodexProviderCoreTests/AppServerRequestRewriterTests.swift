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

    private func modelProviders(in line: String) throws -> Any? {
        let object = try JSONSerialization.jsonObject(
            with: try XCTUnwrap(line.data(using: .utf8))
        )
        let request = try XCTUnwrap(object as? [String: Any])
        let params = try XCTUnwrap(request["params"] as? [String: Any])
        return params["modelProviders"]
    }
}
