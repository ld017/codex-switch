import Foundation
import Darwin
import XCTest
@testable import CodexProfileCore

final class CodexUsageFetchRegressionTests: XCTestCase {
    func testResetCardsWithNullDetailsPreserveCount() throws {
        try self.assertResetCards(details: ",\"credits\":null")
    }

    func testResetCardsWithMissingDetailsPreserveCount() throws {
        try self.assertResetCards(details: "")
    }

    func testResetCardsWithEmptyDetailsHaveUnknownExpiration() throws {
        try self.assertResetCards(details: ",\"credits\":[]")
    }

    func testInvalidRequiredCountStillFailsDecoding() {
        let data = Data("""
        {"rateLimits":{"primary":{"usedPercent":23}},"rateLimitResetCredits":{"availableCount":"unknown","credits":null}}
        """.utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(RPCRateLimitsResponse.self, from: data))
    }

    func testQuotaCallbackRunsBeforeSlowTokenRequest() async throws {
        let cli = try FakeUsageCLI(tokenDelay: "0.4")
        defer { cli.cleanup() }
        let quotaArrived = self.expectation(description: "quota arrives before token")
        let probe = QuotaProbe()
        let task = Task {
            try await CLIUsageFetcher.fetch(
                profileId: "test", authData: Data("{}".utf8), codexConfigURL: cli.configURL,
                environment: cli.environment, clientVersion: "test", onQuota: { snapshot in
                    let tokenRequested = cli.requestLog.contains("account/usage/read")
                    await probe.record(snapshot, tokenRequested: tokenRequested)
                    quotaArrived.fulfill()
                })
        }
        await self.fulfillment(of: [quotaArrived], timeout: 3)
        let result = try await task.value
        let early = await probe.result()
        XCTAssertEqual(early.snapshots.count, 1)
        XCTAssertEqual(early.snapshots.first?.primaryUsedPercent, 23)
        XCTAssertNil(early.snapshots.first?.tokenActivity)
        XCTAssertFalse(early.tokenRequested)
        XCTAssertEqual(result.tokenActivity?.lifetimeTokens, 120)
        XCTAssertEqual(result.tokenActivityUnavailable, false)
        XCTAssertNil(result.tokenActivityError)
    }

    func testSkippingTokenNeverRequestsActivity() async throws {
        let cli = try FakeUsageCLI()
        defer { cli.cleanup() }
        let probe = QuotaProbe()
        let result = try await CLIUsageFetcher.fetch(
            profileId: "test", authData: Data("{}".utf8), codexConfigURL: cli.configURL,
            environment: cli.environment, clientVersion: "test", includeTokenActivity: false,
            onQuota: { await probe.record($0, tokenRequested: false) })
        XCTAssertFalse(cli.requestLog.contains("account/usage/read"))
        XCTAssertEqual(result.primaryUsedPercent, 23)
        XCTAssertNil(result.tokenActivity)
        XCTAssertNil(result.tokenActivityUnavailable)
        XCTAssertNil(result.tokenActivityError)
        let early = await probe.result()
        XCTAssertEqual(early.snapshots.count, 1)
    }

    func testEitherMissingTokenFieldMarksPartialActivityUnavailable() async throws {
        for response in [
            #"{"summary":{"lifetimeTokens":120}}"#,
            #"{"dailyUsageBuckets":[{"startDate":"2026-10-17","tokens":7}]}"#,
        ] {
            let cli = try FakeUsageCLI(tokenResult: response)
            defer { cli.cleanup() }
            let result = try await self.fetch(cli)
            XCTAssertEqual(result.primaryUsedPercent, 23)
            XCTAssertNotNil(result.tokenActivity)
            XCTAssertEqual(result.tokenActivityUnavailable, true)
            XCTAssertNotNil(result.tokenActivityError)
        }
    }

    func testTokenErrorIsShortAndRedactedWhileKeepingQuota() async throws {
        let cli = try FakeUsageCLI(tokenError: "unsupported method user@example.org " + String(repeating: "detail ", count: 100))
        defer { cli.cleanup() }
        let result = try await self.fetch(cli)
        XCTAssertEqual(result.primaryUsedPercent, 23)
        XCTAssertEqual(result.tokenActivityUnavailable, true)
        let error = try XCTUnwrap(result.tokenActivityError)
        XCTAssertTrue(error.contains("unsupported method"))
        XCTAssertFalse(error.contains("user@example.org"))
        XCTAssertLessThanOrEqual(error.count, 260)
    }

    func testTokenFailureFieldRoundTripsAndOldCachesDecode() throws {
        let old = Data(#"{"primaryUsedPercent":20,"secondaryUsedPercent":30,"fetchedAt":0}"#.utf8)
        var snapshot = try JSONDecoder().decode(UsageSnapshot.self, from: old)
        XCTAssertNil(snapshot.tokenActivityError)
        snapshot.tokenActivityError = "unsupported method"
        XCTAssertEqual(try JSONDecoder().decode(UsageSnapshot.self, from: JSONEncoder().encode(snapshot)), snapshot)
    }

    func testCancellationDuringTokenReadReapsCLI() async throws {
        let cli = try FakeUsageCLI(silenceToken: true)
        defer { cli.cleanup() }
        let task = Task { try await self.fetch(cli) }
        let deadline = Date().addingTimeInterval(3)
        while !cli.requestLog.contains("account/usage/read"), Date() < deadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(cli.requestLog.contains("account/usage/read"))
        let pidText = try String(contentsOf: cli.pidURL, encoding: .utf8).trimmingCharacters(in: .whitespacesAndNewlines)
        let pid = try XCTUnwrap(Int32(pidText))
        let started = Date()
        task.cancel()
        do {
            _ = try await task.value
            XCTFail("Cancellation should be propagated")
        } catch is CancellationError {
            // Expected: cancellation must not become a successful quota-only result.
        }
        XCTAssertLessThan(Date().timeIntervalSince(started), 3)
        XCTAssertEqual(kill(pid, 0), -1)
        XCTAssertEqual(errno, ESRCH)
    }

    private func fetch(_ cli: FakeUsageCLI) async throws -> UsageSnapshot {
        try await CLIUsageFetcher.fetch(
            profileId: "test", authData: Data("{}".utf8), codexConfigURL: cli.configURL,
            environment: cli.environment, clientVersion: "test")
    }

    private func assertResetCards(details: String) throws {
        let data = Data("""
        {"rateLimits":{"primary":{"usedPercent":23}},"rateLimitResetCredits":{"availableCount":3\(details)}}
        """.utf8)
        let response = try JSONDecoder().decode(RPCRateLimitsResponse.self, from: data)
        let snapshot = try CLIUsageFetcher.makeSnapshot(
            from: response.rateLimits, resetCredits: response.rateLimitResetCredits)
        XCTAssertEqual(snapshot.primaryUsedPercent, 23)
        XCTAssertEqual(snapshot.resetCreditsAvailable, 3)
        XCTAssertNil(snapshot.nextResetCreditExpiresAt)
    }
}

private actor QuotaProbe {
    private var snapshots: [UsageSnapshot] = []
    private var tokenRequested = false
    func record(_ snapshot: UsageSnapshot, tokenRequested: Bool) {
        self.snapshots.append(snapshot)
        self.tokenRequested = tokenRequested
    }
    func result() -> (snapshots: [UsageSnapshot], tokenRequested: Bool) {
        (self.snapshots, self.tokenRequested)
    }
}

private struct FakeUsageCLI: Sendable {
    let directory: URL
    let environment: [String: String]
    var configURL: URL { self.directory.appendingPathComponent("missing.toml") }
    var pidURL: URL { self.directory.appendingPathComponent("pid") }
    var requestLog: String {
        let log = (try? String(contentsOf: self.directory.appendingPathComponent("requests"), encoding: .utf8)) ?? ""
        return log.replacingOccurrences(of: "\\/", with: "/")
    }

    init(
        tokenResult: String = #"{"summary":{"lifetimeTokens":120},"dailyUsageBuckets":[]}"#,
        tokenError: String? = nil,
        tokenDelay: String = "0",
        silenceToken: Bool = false
    ) throws {
        self.directory = FileManager.default.temporaryDirectory.appendingPathComponent("rpc-regression-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: self.directory, withIntermediateDirectories: true)
        let executable = self.directory.appendingPathComponent("fake-codex")
        let tokenEnvelope: [String: Any]
        if let tokenError {
            tokenEnvelope = ["error": ["message": tokenError]]
        } else {
            tokenEnvelope = ["result": try JSONSerialization.jsonObject(with: Data(tokenResult.utf8))]
        }
        // Responses have no single quotes, so the shell can print them literally.
        let tokenJSON = String(decoding: try JSONSerialization.data(withJSONObject: tokenEnvelope), as: UTF8.self)
        let tokenFields = String(tokenJSON.dropFirst().dropLast())
        let script = """
        #!/bin/sh
        printf '%s\\n' "$$" > "$CODEX_RPC_TEST_PID"
        while IFS= read -r line; do
          printf '%s\\n' "$line" >> "$CODEX_RPC_TEST_LOG"
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\\([0-9]*\\).*/\\1/p')
          case "$line" in
            *rateLimits*) printf '{"id":%s,"result":{"rateLimits":{"primary":{"usedPercent":23}},"rateLimitResetCredits":{"availableCount":3,"credits":null}}}\\n' "$id" ;;
            *usage*)
              \(silenceToken ? "continue" : "sleep \(tokenDelay)")
              printf '{"id":%s,\(tokenFields)}\\n' "$id" ;;
            *initialize*) if [ -n "$id" ]; then printf '{"id":%s,"result":{}}\\n' "$id"; fi ;;
          esac
        done
        """
        try script.write(to: executable, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
        var env = ProcessInfo.processInfo.environment
        env["CODEX_PROFILE_TEST_CLI"] = executable.path
        env["CODEX_RPC_TEST_PID"] = self.directory.appendingPathComponent("pid").path
        env["CODEX_RPC_TEST_LOG"] = self.directory.appendingPathComponent("requests").path
        self.environment = env
    }

    func cleanup() { try? FileManager.default.removeItem(at: self.directory) }
}
