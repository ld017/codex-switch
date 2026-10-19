import Foundation
import Testing
@testable import CodexProfileCore

@Suite("Warm-up Core")
struct WarmupCoreTests {
    @Test("bounded dispatcher handles split JSON-RPC lines")
    func dispatchesSplitLine() throws {
        let dispatcher = WarmupRPCMessageDispatcher(
            limits: WarmupOutputLimits(maxLineBytes: 512, maxBufferedBytes: 1024, maxMessages: 4))
        let first = try dispatcher.append(Data("{\"id\":1,\"type\":\"response.completed\"".utf8))
        #expect(first.isEmpty)
        let second = try dispatcher.append(Data("}\n".utf8))
        #expect(second.count == 1)
        #expect(second[0].id == 1)
        #expect(second[0].isCompletedResponse)
        #expect(try dispatcher.finish().isEmpty)
    }

    @Test("bounded dispatcher fails closed on oversized line and message count")
    func dispatchLimitIsTerminal() throws {
        let lineDispatcher = WarmupRPCMessageDispatcher(
            limits: WarmupOutputLimits(maxLineBytes: 8, maxBufferedBytes: 32, maxMessages: 4))
        #expect(throws: WarmupRPCDispatchError.lineTooLarge(9)) {
            _ = try lineDispatcher.append(Data("123456789\n".utf8))
        }

        let messageDispatcher = WarmupRPCMessageDispatcher(
            limits: WarmupOutputLimits(maxLineBytes: 128, maxBufferedBytes: 256, maxMessages: 1))
        _ = try messageDispatcher.append(Data("{\"id\":1}\n".utf8))
        #expect(throws: WarmupRPCDispatchError.messageLimitExceeded(2)) {
            _ = try messageDispatcher.append(Data("{\"id\":2}\n".utf8))
        }
    }

    @Test("minimal configuration does not copy user configuration")
    func minimalConfiguration() throws {
        let configuration = try WarmupMinimalConfiguration(model: "gpt-5.6-luna")
        #expect(configuration.toml == "model = \"gpt-5.6-luna\"\nmodel_provider = \"openai\"\n")
        #expect(!configuration.toml.contains("api_key"))
        #expect(throws: WarmupConfigurationError.invalidModel) {
            _ = try WarmupMinimalConfiguration(model: "bad\"\nmodel_provider = \"other")
        }
    }

    @Test("confirmation accepts stable reset even when usage remains zero")
    func confirmsStableReset() throws {
        let base = Date(timeIntervalSince1970: 1_000)
        let reset = base.addingTimeInterval(300 * 60)
        let baseline = WarmupQuotaObservation(
            accountIdentity: "account-a",
            observedAt: base,
            windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0, windowDurationMinutes: 300,
                                        resetsAt: reset)])
        let samples = [15.0, 70.0, 80.0].map { offset in
            WarmupQuotaObservation(
                accountIdentity: "account-a",
                observedAt: base.addingTimeInterval(offset),
                windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0,
                                            windowDurationMinutes: 300, resetsAt: reset)])
        }
        let result = WarmupQuotaConfirmation.evaluate(
            baseline: baseline,
            samples: samples,
            policy: WarmupConfirmationPolicy(minimumSpan: 60))
        #expect(result == .confirmed(poolID: "codex", resetAt: reset))
    }

    @Test("moving placeholder, changed identity, or old pool cannot confirm")
    func rejectsAmbiguousConfirmation() throws {
        let base = Date(timeIntervalSince1970: 2_000)
        let reset = base.addingTimeInterval(18_000)
        let baseline = WarmupQuotaObservation(
            accountIdentity: "account-a", observedAt: base,
            windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0,
                                        windowDurationMinutes: 300, resetsAt: reset)])
        let moving = [15.0, 70.0, 80.0].map { offset in
            WarmupQuotaObservation(
                accountIdentity: "account-a", observedAt: base.addingTimeInterval(offset),
                windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0,
                                            windowDurationMinutes: 300,
                                            resetsAt: base.addingTimeInterval(offset + 18_000))])
        }
        #expect(
            WarmupQuotaConfirmation.evaluate(
                baseline: baseline, samples: moving,
                policy: WarmupConfirmationPolicy(minimumSpan: 60))
                == .unconfirmed(.resetStillMoving))

        let wrongIdentity = moving.map { observation in
            WarmupQuotaObservation(accountIdentity: "account-b", observedAt: observation.observedAt,
                                   windows: observation.windows)
        }
        #expect(
            WarmupQuotaConfirmation.evaluate(
                baseline: baseline, samples: wrongIdentity,
                policy: WarmupConfirmationPolicy(minimumSpan: 60))
                == .unconfirmed(.accountIdentityChanged))
    }

    @Test("dispatch refuses an already-active old window")
    func doesNotRewarmActiveWindow() throws {
        let observed = Date(timeIntervalSince1970: 3_000)
        let reset = observed.addingTimeInterval(60)
        let baseline = WarmupQuotaObservation(
            accountIdentity: "account-a", observedAt: observed,
            windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 4,
                                        windowDurationMinutes: 300, resetsAt: reset)])
        #expect(
            WarmupQuotaConfirmation.dispatchDecision(baseline: baseline)
                == .skipExistingWindow(resetAt: reset))
    }

    @Test("confirmation cannot choose a model-specific pool instead of the main codex pool")
    func rejectsModelSpecificPoolAsMain() {
        let base = Date(timeIntervalSince1970: 3_000)
        let reset = base.addingTimeInterval(18_000)
        let observation = WarmupQuotaObservation(accountIdentity: "a", observedAt: base,
            windows: [WarmupQuotaWindow(poolID: "codex-model-extra", usedPercent: 0,
                windowDurationMinutes: 300, resetsAt: reset)])
        let samples = [0.0, 30.0, 60.0].map { offset in
            WarmupQuotaObservation(accountIdentity: "a", observedAt: base.addingTimeInterval(offset),
                                   windows: observation.windows)
        }
        #expect(WarmupQuotaConfirmation.evaluate(baseline: observation, samples: samples)
            == .unconfirmed(.baselinePoolMissing))
    }

    @Test("multiple baselines permit only an explicit moving main-pool placeholder")
    func movingBaselineIsEligible() {
        let base = Date(timeIntervalSince1970: 4_000)
        let baselines = [0.0, 8.0].map { offset in
            WarmupQuotaObservation(accountIdentity: "account-a", observedAt: base.addingTimeInterval(offset),
                windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0, windowDurationMinutes: 300,
                    resetsAt: base.addingTimeInterval(offset + 18_000))])
        }
        #expect(WarmupQuotaConfirmation.dispatchDecision(baselines: baselines) == .send)
        #expect(WarmupQuotaConfirmation.dispatchDecision(baselines: [baselines[0]])
            == .refuse(.insufficientSamples(actual: 1, required: 2)))
    }

    @Test("temporary directory requires its owner before deletion")
    func ownedTemporaryDirectory() throws {
        let root = URL(fileURLWithPath: "/Users/Shared", isDirectory: true)
            .appendingPathComponent("warmup-core-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: root) }

        let owner = try WarmupProcessOwner(runID: "run-1", processIdentifier: 123,
                                           startedAt: Date(timeIntervalSince1970: 10))
        let directory = try WarmupOwnedTemporaryDirectory.create(under: root, owner: owner)
        #expect(FileManager.default.fileExists(atPath: directory.ownerURL.path))
        let wrong = try WarmupProcessOwner(runID: "run-1", processIdentifier: 124,
                                           startedAt: owner.startedAt)
        let wrongDirectory = WarmupOwnedTemporaryDirectory(root: root, url: directory.url, owner: wrong)
        #expect(throws: WarmupOwnerError.ownerMismatch) {
            try wrongDirectory.removeIfOwned()
        }
        try directory.removeIfOwned()
        #expect(!FileManager.default.fileExists(atPath: directory.url.path))
    }
}
