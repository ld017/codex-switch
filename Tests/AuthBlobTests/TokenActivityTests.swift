import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Codex token activity")
struct TokenActivityTests {
    @Test func unavailableActivityEndpointDoesNotDiscardQuotas() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let cli = dir.appendingPathComponent("fake-codex")
        try """
        #!/bin/sh
        while IFS= read -r line; do
          id=$(printf '%s' "$line" | sed -n 's/.*"id":\\([0-9]*\\).*/\\1/p')
          case "$line" in
            *rateLimits*) printf '{"id":%s,"result":{"rateLimits":{"primary":{"usedPercent":23}}}}\\n' "$id" ;;
            *usage*) printf '{"id":%s,"error":{"message":"unsupported method"}}\\n' "$id" ;;
            *initialize*) if [ -n "$id" ]; then printf '{"id":%s,"result":{}}\\n' "$id"; fi ;;
          esac
        done
        """.write(to: cli, atomically: true, encoding: .utf8)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: cli.path)
        var env = ProcessInfo.processInfo.environment
        env["CODEX_PROFILE_TEST_CLI"] = cli.path
        let snapshot = try await CLIUsageFetcher.fetch(profileId: "test", authData: Data("{}".utf8),
            codexConfigURL: dir.appendingPathComponent("missing.toml"), environment: env, clientVersion: "test")
        #expect(snapshot.primaryUsedPercent == 23)
        #expect(snapshot.tokenActivityUnavailable == true)
        #expect(snapshot.tokenActivity == nil)
    }

    @Test func decodesLifetimeAndDailyBuckets() throws {
        let response = try JSONDecoder().decode(RPCTokenActivityResponse.self, from: Data("""
        {"summary":{"lifetimeTokens":145250530},"dailyUsageBuckets":[{"startDate":"2026-10-02","tokens":6907839}]}
        """.utf8))
        #expect(response.activity.lifetimeTokens == 145250530)
        #expect(response.activity.dailyUsageBuckets?.first?.tokens == 6907839)
    }

    @Test func unavailableAndInvalidAreNotZero() throws {
        let response = try JSONDecoder().decode(RPCTokenActivityResponse.self,
            from: Data("{\"summary\":null,\"dailyUsageBuckets\":null}".utf8))
        #expect(response.activity.lifetimeTokens == nil)
        #expect(response.activity.dailyUsageBuckets == nil)
        #expect(TokenActivity(lifetimeTokens: -1, dailyUsageBuckets: nil).lifetimeTokens == nil)
        #expect(TokenActivity(lifetimeTokens: 0, dailyUsageBuckets: nil).lifetimeTokens == 0)
    }

    @Test func oldCacheStillDecodesAndNewActivityRoundTrips() throws {
        let data = Data("""
        {"primaryUsedPercent":20,"secondaryUsedPercent":30,"fetchedAt":0}
        """.utf8)
        var snapshot = try JSONDecoder().decode(UsageSnapshot.self, from: data)
        #expect(snapshot.tokenActivity == nil)
        #expect(snapshot.tokenActivityUnavailable == nil)
        snapshot.tokenActivity = TokenActivity(lifetimeTokens: 9_000_000_000, dailyUsageBuckets: nil)
        snapshot.tokenActivityUnavailable = false
        let roundTrip = try JSONDecoder().decode(UsageSnapshot.self, from: JSONEncoder().encode(snapshot))
        #expect(roundTrip == snapshot)
    }
}
