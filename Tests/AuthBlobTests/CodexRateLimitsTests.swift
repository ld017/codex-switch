@testable import CodexProfileCore
import Foundation
import Testing

@Suite("Codex rate-limit snapshots")
struct CodexRateLimitsTests {

    @Test("usage refresh always selects OpenAI without changing provider tables")
    func usageRefreshForcesOpenAIProvider() throws {
        let source = """
        model = "gpt-test"
        model_provider = "sub2api"

        [model_providers.sub2api]
        name = "Sub2API"
        """
        let result = CLIUsageFetcher.openAIUsageConfiguration(from: source)
        #expect(result.contains("model_provider = \"openai\""))
        #expect(result.contains("[model_providers.sub2api]"))
        #expect(result.contains("name = \"Sub2API\""))
        #expect(!result.contains("model_provider = \"sub2api\""))
    }

    @Test("captures a weekly primary window when secondary is absent")
    func capturesWeeklyPrimaryWindow() throws {
        let json = """
        {
          "rateLimits": {
            "primary": {
              "usedPercent": 18,
              "windowDurationMins": 10080,
              "resetsAt": 1784487871
            },
            "secondary": null,
            "credits": {
              "hasCredits": false,
              "unlimited": false,
              "balance": null
            },
            "planType": "team"
          }
        }
        """

        let response = try JSONDecoder().decode(RPCRateLimitsResponse.self, from: Data(json.utf8))
        let snapshot = try CLIUsageFetcher.makeSnapshot(from: response.rateLimits)

        #expect(snapshot.primaryUsedPercent == 18)
        #expect(snapshot.primaryWindowDurationMins == 10_080)
        #expect(snapshot.secondaryUsedPercent == 0)
        #expect(snapshot.secondaryWindowDurationMins == nil)
    }

    @Test("captures the server count and earliest expiration among available reset cards")
    func capturesResetCards() throws {
        let json = """
        {
          "rateLimits": {
            "primary": {"usedPercent": 20, "windowDurationMins": 300, "resetsAt": 1789394245},
            "secondary": null,
            "credits": {"hasCredits": false, "unlimited": false, "balance": "0"},
            "planType": "plus"
          },
          "rateLimitResetCredits": {
            "availableCount": 3,
            "credits": [
              {"id": "later", "resetType": "codexRateLimits", "status": "available",
               "grantedAt": 1788483522, "expiresAt": 1791075522,
               "title": "Full reset", "description": "Later"},
              {"id": "expired", "resetType": "codexRateLimits", "status": "expired",
               "grantedAt": 1787000000, "expiresAt": 1789000000,
               "title": "Full reset", "description": "Expired"},
              {"id": "nearest", "resetType": "codexRateLimits", "status": "available",
               "grantedAt": 1787352360, "expiresAt": 1789944360,
               "title": "Full reset", "description": "Nearest"}
            ]
          }
        }
        """
        let response = try JSONDecoder().decode(RPCRateLimitsResponse.self, from: Data(json.utf8))
        let snapshot = try CLIUsageFetcher.makeSnapshot(
            from: response.rateLimits,
            resetCredits: response.rateLimitResetCredits)

        #expect(snapshot.resetCreditsAvailable == 3)
        #expect(snapshot.nextResetCreditExpiresAt == Date(timeIntervalSince1970: 1789944360))
    }


    @Test("decodes snapshots cached before window durations were recorded")
    func decodesLegacySnapshot() throws {
        let json = """
        {
          "planType": "team",
          "creditsRemaining": null,
          "primaryUsedPercent": 18,
          "primaryResetAt": "2026-07-19T20:00:00Z",
          "secondaryUsedPercent": 0,
          "secondaryResetAt": null,
          "fetchedAt": "2026-07-14T20:00:00Z"
        }
        """
        let decoder = JSONDecoder()
        decoder.dateDecodingStrategy = .iso8601

        let snapshot = try decoder.decode(UsageSnapshot.self, from: Data(json.utf8))

        #expect(snapshot.primaryWindowDurationMins == nil)
        #expect(snapshot.secondaryWindowDurationMins == nil)
        #expect(snapshot.resetCreditsAvailable == nil)
        #expect(snapshot.nextResetCreditExpiresAt == nil)
    }
}
