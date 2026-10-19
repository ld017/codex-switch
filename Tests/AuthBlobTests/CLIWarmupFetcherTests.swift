import Darwin
import Foundation
import XCTest
@testable import CodexProfileCore

final class CLIWarmupFetcherTests: XCTestCase {
    func testIdentitySurvivesAnAddedIDTokenAndRotatedRefreshToken() throws {
        let fake = try FakeWarmupCLI()
        defer { fake.cleanup() }
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: fake.authData) as? [String: Any])
        var tokens = try XCTUnwrap(object["tokens"] as? [String: Any])
        let payload = try JSONSerialization.data(withJSONObject: ["sub": "stable-user"]).base64EncodedString()
        tokens["id_token"] = "e30.\(payload).sig"
        tokens["refresh_token"] = "rotated-fixture"
        object["tokens"] = tokens
        let replacement = try JSONSerialization.data(withJSONObject: object)
        XCTAssertNotEqual(AuthBlob.identityFingerprint(from: fake.authData), AuthBlob.identityFingerprint(from: replacement))
        XCTAssertEqual(WarmupAccountIdentity.fingerprint(from: fake.authData), WarmupAccountIdentity.fingerprint(from: replacement))
        XCTAssertNotNil(WarmupAccountIdentity.fingerprint(from: replacement))
    }
    func testObserveAuthenticatesInMemoryReadsRawPoolsAndReapsProcess() async throws {
        let fake = try FakeWarmupCLI()
        defer { fake.cleanup() }
        let observation = try await CLIWarmupFetcher.observe(authData: fake.authData,
            tempRoot: fake.homes, environment: fake.environment, clientVersion: "test")
        XCTAssertEqual(observation.accountIdentity, WarmupAccountIdentity.fingerprint(from: fake.authData))
        XCTAssertEqual(observation.windows.first?.poolID, "codex")
        XCTAssertEqual(observation.windows.first?.windowDurationMinutes, 300)
        XCTAssertEqual(observation.windows.first?.usedPercent, 0)
        XCTAssertFalse(fake.methods.contains("turn/start"))
        XCTAssertTrue(try fake.noLiveProcess())
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: fake.homes.path), [])
    }

    func testSendWaitsForMatchingCompletedTurnThenReturnsQuota() async throws {
        let fake = try FakeWarmupCLI(mode: "out-of-order")
        defer { fake.cleanup() }
        let calls = WarmupCallbackCounter()
        let result = try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
            expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
            tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
            onBeforeSend: { await calls.increment() })
        XCTAssertTrue(result.completed)
        XCTAssertEqual(result.model, "gpt-6-luna")
        XCTAssertEqual(result.firstObservation?.windows.first?.poolID, "codex")
        XCTAssertGreaterThanOrEqual(result.completedAt, result.sentAt)
        let count = await calls.value
        XCTAssertEqual(count, 1)
        XCTAssertEqual(fake.methods.filter { $0 == "turn/start" }.count, 1)
        XCTAssertTrue(try fake.noLiveProcess())
    }

    func testThrowingSendIntentNeverSendsTurn() async throws {
        let fake = try FakeWarmupCLI()
        defer { fake.cleanup() }
        do {
            _ = try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
                expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                onBeforeSend: { throw TestIntentError.refused })
            XCTFail("Send intent failure must escape")
        } catch TestIntentError.refused {}
        XCTAssertFalse(fake.methods.contains("turn/start"))
        XCTAssertTrue(try fake.noLiveProcess())
    }

    func testIdentityMismatchAndNonEphemeralThreadFailBeforeSend() async throws {
        for mode in ["identity-mismatch", "persistent-thread"] {
            let fake = try FakeWarmupCLI(mode: mode)
            defer { fake.cleanup() }
            do {
                _ = try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
                    expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
                    tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                    onBeforeSend: {})
                XCTFail("Unsafe identity/thread must fail: \(mode)")
            } catch is CLIWarmupError {}
            XCTAssertFalse(fake.methods.contains("turn/start"))
            XCTAssertTrue(try fake.noLiveProcess())
        }
    }

    func testQuotaConflictAndOutputOverflowFailClosedWithoutTurn() async throws {
        for mode in ["quota-conflict", "overflow"] {
            let fake = try FakeWarmupCLI(mode: mode)
            defer { fake.cleanup() }
            do {
                _ = try await CLIWarmupFetcher.observe(authData: fake.authData,
                    tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                    limits: WarmupSessionLimits(maxOutputBytes: 8192, maxLineBytes: 2048))
                XCTFail("Conflicting quota or overflow must fail closed")
            } catch is CLIWarmupError {}
            XCTAssertFalse(fake.methods.contains("turn/start"))
            XCTAssertTrue(try fake.noLiveProcess())
        }
    }

    func testMissingIdentityRejectsBeforeLaunchingChild() async throws {
        let fake = try FakeWarmupCLI()
        defer { fake.cleanup() }
        do {
            _ = try await CLIWarmupFetcher.observe(authData: Data("{}".utf8),
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test")
            XCTFail("Auth must be validated before process launch")
        } catch CLIWarmupError.invalidAuth {}
        XCTAssertTrue(fake.methods.isEmpty)
        XCTAssertFalse(FileManager.default.fileExists(atPath: fake.directory.appendingPathComponent("pid").path))
    }

    func testFinalPreflightBlocksAWindowStartedByAnotherClient() async throws {
        let fake = try FakeWarmupCLI(mode: "became-active")
        defer { fake.cleanup() }
        let calls = WarmupCallbackCounter()
        do {
            _ = try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
                expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                onBeforeSend: { await calls.increment() })
            XCTFail("A concurrently started window must not be warmed")
        } catch CLIWarmupError.existingWindow {}
        let count = await calls.value
        XCTAssertEqual(count, 0)
        XCTAssertFalse(fake.methods.contains("turn/start"))
        XCTAssertTrue(try fake.noLiveProcess())
    }

    func testTerminalTurnItemsCannotHideToolActivity() async throws {
        let fake = try FakeWarmupCLI(mode: "terminal-tool")
        defer { fake.cleanup() }
        do {
            _ = try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
                expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test", onBeforeSend: {})
            XCTFail("Completed turn with a tool item must fail")
        } catch CLIWarmupError.unexpectedToolActivity {}
        XCTAssertTrue(try fake.noLiveProcess())
    }

    func testAChildIgnoringTermIsKilledAndReapedWithinCloseBudget() async throws {
        let fake = try FakeWarmupCLI(mode: "ignore-term")
        defer { fake.cleanup() }
        let started = ContinuousClock.now
        do {
            _ = try await CLIWarmupFetcher.observe(authData: fake.authData,
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                limits: WarmupSessionLimits(requestTimeout: 5))
            XCTFail("Unresponsive child must timeout")
        } catch is CLIWarmupError {}
        XCTAssertLessThan(started.duration(to: .now), .seconds(8))
        XCTAssertTrue(try fake.noLiveProcess())
    }

    func testWrongTurnCompletionTimesOutAndCancellationReapsChild() async throws {
        let fake = try FakeWarmupCLI(mode: "wrong-completion")
        defer { fake.cleanup() }
        let task = Task {
            try await CLIWarmupFetcher.sendMinimal(authData: fake.authData,
                expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: fake.authData)), baselines: fake.baselines,
                tempRoot: fake.homes, environment: fake.environment, clientVersion: "test",
                limits: WarmupSessionLimits(requestTimeout: 5, turnTimeout: 0.2), onBeforeSend: {})
        }
        do {
            _ = try await task.value
            XCTFail("Foreign turn completion must not complete this turn")
        } catch let error as CLIWarmupError {
            XCTAssertTrue(error == .timedOut("turn/completed") || error == .serverClosed)
        }
        XCTAssertTrue(fake.methods.contains("turn/start"), "Exercise turn timeout after child startup, not interpreter startup timeout")
        XCTAssertTrue(try fake.noLiveProcess())

        let cancelFake = try FakeWarmupCLI(mode: "wrong-completion")
        defer { cancelFake.cleanup() }
        let cancelled = Task {
            try await CLIWarmupFetcher.sendMinimal(authData: cancelFake.authData,
                expectedIdentity: try XCTUnwrap(WarmupAccountIdentity.fingerprint(from: cancelFake.authData)), baselines: cancelFake.baselines,
                tempRoot: cancelFake.homes, environment: cancelFake.environment, clientVersion: "test",
                onBeforeSend: {})
        }
        let startupDeadline = ContinuousClock.now.advanced(by: .seconds(5))
        while !cancelFake.methods.contains("turn/start"), ContinuousClock.now < startupDeadline {
            try await Task.sleep(for: .milliseconds(10))
        }
        XCTAssertTrue(cancelFake.methods.contains("turn/start"), "Reach the pending turn before testing cancellation")
        cancelled.cancel()
        do { _ = try await cancelled.value; XCTFail("Cancellation must escape") }
        catch is CancellationError {}
        XCTAssertTrue(try cancelFake.noLiveProcess())
    }
}

private enum TestIntentError: Error { case refused }
private actor WarmupCallbackCounter {
    var value = 0
    func increment() { value += 1 }
}

private struct FakeWarmupCLI: Sendable {
    let directory: URL
    let homes: URL
    let environment: [String: String]
    let authData: Data
    var baselines: [WarmupQuotaObservation] {
        let now = Date()
        return [-8.0, -1.0].map { offset in
            let observed = now.addingTimeInterval(offset)
            return WarmupQuotaObservation(accountIdentity: WarmupAccountIdentity.fingerprint(from: authData), observedAt: observed,
                windows: [WarmupQuotaWindow(poolID: "codex", usedPercent: 0, windowDurationMinutes: 300,
                                            resetsAt: observed.addingTimeInterval(18000))])
        }
    }
    var methods: [String] {
        ((try? String(contentsOf: directory.appendingPathComponent("methods"), encoding: .utf8)) ?? "")
            .split(separator: "\n").map(String.init)
    }
    init(mode: String = "normal") throws {
        directory = URL(fileURLWithPath: "/Users/Shared", isDirectory: true).appendingPathComponent("fake-warmup-\(UUID())")
        homes = directory.appendingPathComponent("homes")
        try FileManager.default.createDirectory(at: homes, withIntermediateDirectories: true)
        func base64(_ object: [String: Any]) throws -> String {
            try JSONSerialization.data(withJSONObject: object).base64EncodedString()
                .replacingOccurrences(of: "+", with: "-").replacingOccurrences(of: "/", with: "_")
                .replacingOccurrences(of: "=", with: "")
        }
        let payload = try base64(["email": "fixture@example.invalid", "exp": Date().addingTimeInterval(3600).timeIntervalSince1970,
            "https://api.openai.com/auth": ["chatgpt_account_id": "workspace-fixture"]])
        authData = try JSONSerialization.data(withJSONObject: ["tokens": ["access_token": "e30.\(payload).sig",
            "account_id": "workspace-fixture", "refresh_token": "MUST-NOT-LEAVE-PARENT"]])
        let executable = directory.appendingPathComponent("fake-codex")
        let script = #"""
#!/usr/bin/python3
import json,sys,os,pathlib,time,signal
root=pathlib.Path(__file__).parent
(root/'pid').write_text(str(os.getpid()))
home=pathlib.Path(os.environ['CODEX_HOME'])
mode=__MODE__
if mode=='ignore-term':
 signal.signal(signal.SIGTERM,signal.SIG_IGN)
 while True: time.sleep(1)
def emit(value):
 print(json.dumps(value),flush=True)
def result(i,value):
 emit({'id':i,'result':value})
for raw in sys.stdin:
 m=json.loads(raw); method=m.get('method',''); p=m.get('params',{}); i=m.get('id')
 with (root/'methods').open('a') as log: log.write(method+'\n')
 assert not (home/'auth.json').exists(), 'credentials were persisted'
 assert 'MUST-NOT-LEAVE-PARENT' not in raw, 'refresh token left parent'
 if method=='initialize':
  assert p['capabilities']['experimentalApi'] is True
  assert 'cli_auth_credentials_store = "ephemeral"' in (home/'config.toml').read_text()
  result(i,{'userAgent':'fake'})
 elif method=='initialized': pass
 elif method=='account/login/start':
  assert p['type']=='chatgptAuthTokens' and p['chatgptAccountId']=='workspace-fixture'
  assert set(p).issubset({'type','accessToken','chatgptAccountId'})
  result(i,{'type':'chatgptAuthTokens'})
 elif method=='account/read':
  assert p.get('refreshToken') is False
  result(i,{'account':{'type':'chatgpt','email':'other@example.invalid' if mode=='identity-mismatch' else 'fixture@example.invalid','planType':'plus'},'requiresOpenaiAuth':True})
 elif method=='account/rateLimits/read':
  if mode=='overflow':
   print('x'*10000,flush=True); continue
  pool={'limitId':'codex','primary':{'usedPercent':10 if mode=='became-active' else 0,'windowDurationMins':300,'resetsAt':int(time.time())+18000},'secondary':None}
  alternate=dict(pool)
  if mode=='quota-conflict': alternate['primary']={'usedPercent':9,'windowDurationMins':300,'resetsAt':1}
  result(i,{'rateLimits':pool,'rateLimitsByLimitId':{'codex':alternate}})
 elif method=='model/list':
  result(i,{'data':[{'id':'gpt-6-luna','model':'gpt-6-luna','hidden':False,'supportedReasoningEfforts':[{'reasoningEffort':'low','description':'Low'}],'defaultReasoningEffort':'low'}],'nextCursor':None})
 elif method=='thread/start':
  assert p['ephemeral'] is True and p['approvalPolicy']=='never' and p['sandbox']=='read-only'
  result(i,{'thread':{'id':'thread-fixture','ephemeral':mode!='persistent-thread'},'model':'gpt-6-luna','modelProvider':'openai','sandbox':{'type':'readOnly'},'instructionSources':[]})
 elif method=='turn/start':
  assert p['threadId']=='thread-fixture' and p['input']==[{'type':'text','text':'只回复 OK'}]
  assert p['effort']=='low'
  # Notifications may arrive before the start response; retain them by IDs.
  emit({'method':'turn/completed','params':{'threadId':'foreign','turn':{'id':'foreign','status':'completed'}}})
  if mode=='out-of-order':
   emit({'method':'turn/completed','params':{'threadId':'thread-fixture','turn':{'id':'turn-fixture','status':'completed','items':[{'type':'commandExecution'}] if mode=='terminal-tool' else []}}})
  result(i,{'turn':{'id':'turn-fixture','status':'inProgress'}})
  if mode not in ('wrong-completion','out-of-order'):
   emit({'method':'turn/completed','params':{'threadId':'thread-fixture','turn':{'id':'turn-fixture','status':'completed','items':[{'type':'commandExecution'}] if mode=='terminal-tool' else []}}})
 else: raise AssertionError('unexpected method '+method)
"""#.replacingOccurrences(of: "__MODE__", with: "'\(mode)'")
        try Data(script.utf8).write(to: executable)
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: executable.path)
        environment = ["CODEX_PROFILE_TEST_CLI": executable.path, "PATH": "/usr/bin:/bin", "HOME": directory.path]
    }
    func noLiveProcess() throws -> Bool {
        let text = try String(contentsOf: directory.appendingPathComponent("pid"), encoding: .utf8)
        guard let pid = Int32(text) else { return false }
        return kill(pid, 0) == -1 && errno == ESRCH
    }
    func cleanup() { try? FileManager.default.removeItem(at: directory) }
}
