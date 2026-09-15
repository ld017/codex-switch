import XCTest
@testable import CodexProviderCore

final class ConfigEditorTests: XCTestCase {
    func testReadsKnownProviders() throws {
        XCTAssertEqual(
            try ConfigEditor.currentProvider(
                in: "model = \"gpt-5.6-sol\"\nmodel_provider = \"openai\"\n"
            ),
            .openai
        )
        XCTAssertEqual(
            try ConfigEditor.currentProvider(
                in: "model_provider = \"sub2api\"\n"
            ),
            .sub2api
        )
    }

    func testReadsMissingProviderAsCodexDefaultOpenAIWhenModelExists() throws {
        XCTAssertEqual(
            try ConfigEditor.currentProvider(
                in: "model = \"gpt-5.6-sol\"\npersonality = \"pragmatic\"\n"
            ),
            .openai
        )
    }

    func testMissingProviderWithoutTopLevelModelStillFails() {
        XCTAssertThrowsError(
            try ConfigEditor.currentProvider(
                in: "personality = \"pragmatic\"\n"
            )
        ) { error in
            XCTAssertEqual(error as? ConfigEditorError, .missingProvider)
        }
    }

    func testReplacesOnlyTopLevelProviderAndPreservesOtherBytes() throws {
        let source = """
        model = "gpt-5.6-sol"
        model_provider = "sub2api"
        personality = "pragmatic"

        [model_providers.sub2api]
        name = "Sub2API Local"
        """

        let output = try ConfigEditor.replacingProvider(in: source, with: .openai)

        XCTAssertEqual(
            output,
            source.replacingOccurrences(
                of: "model_provider = \"sub2api\"",
                with: "model_provider = \"openai\""
            )
        )
    }

    func testInsertsMissingProviderImmediatelyAfterModel() throws {
        let source = "model = \"gpt-5.6-sol\"\r\npersonality = \"pragmatic\"\r\n"

        XCTAssertEqual(
            try ConfigEditor.replacingProvider(in: source, with: .sub2api),
            "model = \"gpt-5.6-sol\"\r\nmodel_provider = \"sub2api\"\r\npersonality = \"pragmatic\"\r\n"
        )
    }

    func testRejectsDuplicateTopLevelProviders() {
        let source = """
        model_provider = "openai"
        model_provider = "sub2api"
        """

        XCTAssertThrowsError(
            try ConfigEditor.replacingProvider(in: source, with: .openai)
        ) { error in
            XCTAssertEqual(error as? ConfigEditorError, .duplicateProvider)
        }
    }

    func testIgnoresAssignmentsInsideTables() throws {
        let source = """
        model_provider = "openai"
        [example]
        model_provider = "sub2api"
        """

        XCTAssertEqual(
            try ConfigEditor.replacingProvider(in: source, with: .sub2api),
            """
            model_provider = "sub2api"
            [example]
            model_provider = "sub2api"
            """
        )
    }

    func testRequiresModelAnchorWhenProviderIsMissing() {
        XCTAssertThrowsError(
            try ConfigEditor.replacingProvider(
                in: "personality = \"pragmatic\"\n",
                with: .openai
            )
        ) { error in
            XCTAssertEqual(error as? ConfigEditorError, .missingModelAnchor)
        }
    }

    func testDetectsCompleteSub2APIConfiguration() {
        let complete = """
        [model_providers.sub2api]
        name = "Sub2API Local"
        [model_providers.sub2api.auth]
        command = "/usr/bin/security"
        """
        let incomplete = """
        [model_providers.sub2api]
        name = "Sub2API Local"
        """

        XCTAssertTrue(ConfigEditor.hasSub2APIConfiguration(in: complete))
        XCTAssertFalse(ConfigEditor.hasSub2APIConfiguration(in: incomplete))
    }
}
