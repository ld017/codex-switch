import Darwin
import Foundation
import CryptoKit

public enum CLIWarmupError: Error, LocalizedError, Equatable, Sendable {
    case invalidAuth
    case accessTokenExpired
    case identityMismatch
    case externalAuthNotConfirmed
    case ephemeralThreadNotConfirmed
    case unsafeThreadConfiguration
    case minimalModelUnavailable
    case malformedProtocol
    case unsupportedServerRequest
    case outputLimitExceeded
    case timedOut(String)
    case rpcRejected(method: String, code: Int?)
    case serverClosed
    case turnFailed
    case unexpectedToolActivity
    case quotaPoolConflict
    case processDidNotExit(pid: Int32)
    case existingWindow(resetAt: Date)
    case eligibilityChanged

    public var errorDescription: String? {
        // No server-controlled error message or token is included here.
        switch self {
        case .invalidAuth: return "预热需要可核对身份的 ChatGPT 访问令牌"
        case .accessTokenExpired: return "访问令牌已过期或剩余有效期不足，请先续期"
        case .identityMismatch: return "预热账号身份核对失败"
        case .externalAuthNotConfirmed: return "CLI 未确认内存外部令牌认证"
        case .ephemeralThreadNotConfirmed: return "CLI 未确认临时会话模式"
        case .unsafeThreadConfiguration: return "CLI 会话隔离配置未确认"
        case .minimalModelUnavailable: return "账号目录中没有可用的 Luna 低推理模型"
        case .malformedProtocol: return "CLI 返回不支持的协议数据"
        case .unsupportedServerRequest: return "CLI 请求了预热不允许的工具或认证操作"
        case .outputLimitExceeded: return "CLI 输出超过预热资源上限"
        case .timedOut(let method): return "预热 CLI 超时：\(method)"
        case .rpcRejected(let method, let code): return "预热 CLI 拒绝 \(method)（\(code.map(String.init) ?? "unknown")）"
        case .serverClosed: return "CLI 在协议完成前退出"
        case .turnFailed: return "预热 turn 未成功完成"
        case .unexpectedToolActivity: return "预热出现未授权工具活动"
        case .quotaPoolConflict: return "服务端额度池标识或数据冲突"
        case .processDidNotExit: return "预热 CLI 尚未确认退出，保留运行目录"
        case .existingWindow: return "最终检查发现已有活动窗口，未发送"
        case .eligibilityChanged: return "最终检查无法确认空闲窗口，未发送"
        }
    }
}

public struct WarmupSessionLimits: Sendable {
    public let requestTimeout: TimeInterval
    public let turnTimeout: TimeInterval
    public let maxOutputBytes: Int
    public let maxLineBytes: Int
    public let maxQueuedMessages: Int

    public init(requestTimeout: TimeInterval = 15, turnTimeout: TimeInterval = 90,
                maxOutputBytes: Int = 4 * 1024 * 1024, maxLineBytes: Int = 256 * 1024,
                maxQueuedMessages: Int = 128) {
        self.requestTimeout = max(0.01, requestTimeout)
        self.turnTimeout = max(0.01, turnTimeout)
        self.maxOutputBytes = max(1, maxOutputBytes)
        self.maxLineBytes = max(1, maxLineBytes)
        self.maxQueuedMessages = max(1, maxQueuedMessages)
    }
}

public struct WarmupSendResult: Sendable {
    public let completed: Bool
    public let firstObservation: WarmupQuotaObservation?
    public let model: String
    public let sentAt: Date
    public let completedAt: Date

    public init(completed: Bool, firstObservation: WarmupQuotaObservation?, model: String,
                sentAt: Date, completedAt: Date) {
        self.completed = completed
        self.firstObservation = firstObservation
        self.model = model
        self.sentAt = sentAt
        self.completedAt = completedAt
    }
}

public enum CLIWarmupFetcher {
    public static func observe(
        authData: Data, tempRoot: URL,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        clientVersion: String,
        limits: WarmupSessionLimits = WarmupSessionLimits()
    ) async throws -> WarmupQuotaObservation {
        let auth = try WarmupAccessIdentity(authData: authData, minimumValidity: limits.requestTimeout * 3)
        return try await self.withSession(auth: auth, tempRoot: tempRoot, environment: environment,
                                         clientVersion: clientVersion, limits: limits) { session in
            try await session.observe(identity: auth.identity)
        }
    }

    public static func sendMinimal(
        authData: Data, expectedIdentity: String, baselines: [WarmupQuotaObservation] = [], tempRoot: URL,
        environment: [String: String] = ProcessInfo.processInfo.environment,
        clientVersion: String,
        limits: WarmupSessionLimits = WarmupSessionLimits(),
        onBeforeSend: @escaping @Sendable () async throws -> Void
    ) async throws -> WarmupSendResult {
        let auth = try WarmupAccessIdentity(authData: authData, minimumValidity: limits.turnTimeout + 60)
        guard auth.identity == expectedIdentity else { throw CLIWarmupError.identityMismatch }
        return try await self.withSession(auth: auth, tempRoot: tempRoot, environment: environment,
                                         clientVersion: clientVersion, limits: limits) { session in
            let selected = try await session.selectMinimalModel()
            let threadID = try await session.startThread(model: selected.model)
            let current = try await session.observe(identity: auth.identity)
            guard !baselines.isEmpty else { throw CLIWarmupError.eligibilityChanged }
            let decision = WarmupQuotaConfirmation.dispatchDecision(baselines: baselines + [current])
            switch decision {
            case .send: break
            case .skipExistingWindow(let reset): throw CLIWarmupError.existingWindow(resetAt: reset)
            case .refuse: throw CLIWarmupError.eligibilityChanged
            }
            try Task.checkCancellation()
            // The caller commits its durable send intent here. No turn/start
            // bytes have been sent if it throws, and this closure is never retried.
            try await onBeforeSend()
            try Task.checkCancellation()
            let sentAt = Date()
            let turnID = try await session.sendTurn(threadID: threadID, effort: selected.effort)
            let completedAt = Date()
            // A completed turn is not evidence of a started quota window. Keep
            // completion even when the first quota read fails; the caller then
            // performs bounded verification in fresh processes, never re-sends.
            let observation: WarmupQuotaObservation?
            do { observation = try await session.observe(identity: auth.identity) }
            catch is CancellationError { throw CancellationError() }
            catch let error as CLIWarmupError {
                switch error {
                case .rpcRejected(method: "account/rateLimits/read", code: _),
                     .timedOut("account/rateLimits/read"), .serverClosed: observation = nil
                default: throw error
                }
            }
            try session.checkToolEvents(threadID: threadID, turnID: turnID)
            return WarmupSendResult(completed: true, firstObservation: observation,
                                    model: selected.model, sentAt: sentAt, completedAt: completedAt)
        }
    }

    private static func withSession<T: Sendable>(
        auth: WarmupAccessIdentity, tempRoot: URL, environment: [String: String],
        clientVersion: String, limits: WarmupSessionLimits,
        body: (WarmupCLISession) async throws -> T
    ) async throws -> T {
        try Task.checkCancellation()
        guard let executable = CodexCLIResolver.resolvePath(environment: environment) else {
            throw CodexRPCError.cliNotFound
        }
        let owner = try WarmupProcessOwner.current(runID: UUID().uuidString)
        let directory = try WarmupOwnedTemporaryDirectory.create(under: tempRoot, owner: owner)
        var session: WarmupCLISession?
        do {
            let instance = try WarmupCLISession(executable: executable, home: directory.url,
                                                environment: environment, limits: limits)
            session = instance
            let result = try await withTaskCancellationHandler {
                try await instance.open(auth: auth, clientVersion: clientVersion)
                return try await body(instance)
            } onCancel: {
                instance.interrupt()
            }
            try await instance.close()
            try directory.removeIfOwned()
            return result
        } catch {
            let original = error
            if let session {
                // Do not delete a home while its child may still be using it.
                try await session.close()
            }
            try? directory.removeIfOwned()
            if Task.isCancelled { throw CancellationError() }
            throw original
        }
    }
}

/// The access-token email and workspace remain stable when a refresh adds an
/// id_token to a saved blob. This also groups aliases with different token shapes.
public enum WarmupAccountIdentity {
    public static func fingerprint(from data: Data) -> String? {
        try? WarmupAccessIdentity(authData: data, minimumValidity: 0, requireUnexpired: false).identity
    }
}

/// Only these two token fields are decoded from auth.json. refresh_token and
/// id_token are neither represented nor serialized into the child process.
private struct WarmupAccessIdentity: Sendable {
    let accessToken: String
    let accountID: String
    let email: String
    let identity: String

    init(authData: Data, minimumValidity: TimeInterval, requireUnexpired: Bool = true) throws {
        struct Blob: Decodable {
            struct Tokens: Decodable {
                let access_token: String?
                let accessToken: String?
                let account_id: String?
                let accountId: String?
            }
            let tokens: Tokens
        }
        guard authData.count <= 128 * 1024,
              let blob = try? JSONDecoder().decode(Blob.self, from: authData),
              let accessToken = blob.tokens.access_token ?? blob.tokens.accessToken,
              !accessToken.isEmpty, accessToken.utf8.count <= 8192,
              let accountID = blob.tokens.account_id ?? blob.tokens.accountId,
              !accountID.isEmpty else {
            throw CLIWarmupError.invalidAuth
        }
        let parts = accessToken.split(separator: ".", omittingEmptySubsequences: false)
        guard parts.count == 3 else { throw CLIWarmupError.invalidAuth }
        var base64 = String(parts[1]).replacingOccurrences(of: "-", with: "+")
            .replacingOccurrences(of: "_", with: "/")
        base64 += String(repeating: "=", count: (4 - base64.count % 4) % 4)
        guard let payload = Data(base64Encoded: base64),
              let claims = try? JSONSerialization.jsonObject(with: payload) as? [String: Any],
              let auth = claims["https://api.openai.com/auth"] as? [String: Any],
              auth["chatgpt_account_id"] as? String == accountID,
              let email = (claims["email"] as? String)
                ?? ((claims["https://api.openai.com/profile"] as? [String: Any])?["email"] as? String),
              !email.isEmpty else { throw CLIWarmupError.identityMismatch }
        guard let expiration = claims["exp"] as? NSNumber,
              expiration.doubleValue.isFinite,
              (!requireUnexpired || expiration.doubleValue > Date().timeIntervalSince1970 + minimumValidity) else {
            throw CLIWarmupError.accessTokenExpired
        }
        self.accessToken = accessToken
        self.accountID = accountID
        self.email = email.lowercased()
        self.identity = SHA256.hash(data: try JSONEncoder().encode([accountID, email.lowercased()])).map { String(format: "%02x", $0) }.joined()
    }
}

/// One owner, one consumer of stdout, and one request at a time. Notifications
/// received before a request response are retained only for turn/item terminal
/// events and are bounded; unrelated streaming deltas are discarded promptly.
private final class WarmupCLISession: @unchecked Sendable {
    private let process = Process()
    private let input = Pipe()
    private let output = Pipe()
    private let errors = Pipe()
    private let limits: WarmupSessionLimits
    private let home: URL
    private let work: URL
    private let stream: AsyncThrowingStream<Data, Error>
    private let continuation: AsyncThrowingStream<Data, Error>.Continuation
    private var iterator: AsyncThrowingStream<Data, Error>.Iterator
    private var nextID = 1
    private var events: [WarmupJSONValue] = []
    private let readLock = NSLock()
    private var partial = Data()
    private var outputBytes = 0
    private var terminalReadError: CLIWarmupError?

    init(executable: String, home: URL, environment: [String: String], limits: WarmupSessionLimits) throws {
        self.limits = limits
        self.home = home
        self.work = home.appendingPathComponent("work", isDirectory: true)
        try FileManager.default.createDirectory(at: self.work, withIntermediateDirectories: false,
                                                 attributes: [.posixPermissions: 0o700])
        try AtomicFileWriter.write(Data(Self.configuration.utf8), to: home.appendingPathComponent("config.toml"))
        var continuation: AsyncThrowingStream<Data, Error>.Continuation!
        self.stream = AsyncThrowingStream(bufferingPolicy: .bufferingOldest(limits.maxQueuedMessages)) {
            continuation = $0
        }
        self.continuation = continuation
        self.iterator = self.stream.makeAsyncIterator()
        let allowed = Set(["HOME", "PATH", "USER", "LOGNAME", "TMPDIR", "LANG", "LC_ALL",
                           "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
                           "http_proxy", "https_proxy", "all_proxy", "no_proxy", "SSL_CERT_FILE", "SSL_CERT_DIR"])
        var env = environment.filter { allowed.contains($0.key) }
        env["CODEX_HOME"] = home.path
        env["PATH"] = CodexCLIResolver.effectivePATH(environment: environment)
        self.process.executableURL = URL(fileURLWithPath: executable)
        self.process.arguments = ["-s", "read-only", "app-server", "--stdio"]
        self.process.currentDirectoryURL = self.work
        self.process.environment = env
        self.process.standardInput = self.input
        self.process.standardOutput = self.output
        self.process.standardError = self.errors
        self.output.fileHandleForReading.readabilityHandler = { [weak self] handle in
            guard let self else { return }
            self.receive(handle.availableData)
        }
        // Deliberately discard stderr rather than retaining raw server output.
        self.errors.fileHandleForReading.readabilityHandler = { [weak self] handle in
            guard let self else { return }
            let data = handle.availableData
            if data.isEmpty { handle.readabilityHandler = nil; return }
            self.countOutput(data.count)
        }
        let fd = self.input.fileHandleForWriting.fileDescriptor
        guard fcntl(fd, F_SETFL, fcntl(fd, F_GETFL) | O_NONBLOCK) != -1,
              fcntl(fd, F_SETNOSIGPIPE, 1) != -1 else {
            self.output.fileHandleForReading.readabilityHandler = nil
            self.errors.fileHandleForReading.readabilityHandler = nil
            self.continuation.finish()
            throw CLIWarmupError.malformedProtocol
        }
        do { try self.process.run(); WarmupResources.register(self.process.processIdentifier) }
        catch {
            self.output.fileHandleForReading.readabilityHandler = nil
            self.errors.fileHandleForReading.readabilityHandler = nil
            self.continuation.finish()
            throw CodexRPCError.startFailed("Unable to launch isolated CLI")
        }
    }

    static let configuration = """
    model_provider = "openai"
    cli_auth_credentials_store = "ephemeral"
    web_search = "disabled"
    [analytics]
    enabled = false
    [features]
    shell_tool = false
    unified_exec = false
    shell_snapshot = false
    multi_agent = false
    goals = false
    sleep_tool = false
    hooks = false
    plugins = false
    apps = false
    computer_use = false
    browser_use = false
    in_app_browser = false
    view_image = false
    image_generation = false
    memories = false
    workspace_dependencies = false
    skip_host_skill_discovery = true
    unbounded_connection_retries = false

    """

    func open(auth: WarmupAccessIdentity, clientVersion: String) async throws {
        _ = try await self.request("initialize", params: .object([
            "clientInfo": .object(["name": .string("codex_switch_warmup"), "version": .string(clientVersion)]),
            "capabilities": .object(["experimentalApi": .bool(true)])]))
        try await self.write(.object(["method": .string("initialized"), "params": .object([:])]))
        let login = try await self.request("account/login/start", params: .object([
            "type": .string("chatgptAuthTokens"), "accessToken": .string(auth.accessToken),
            "chatgptAccountId": .string(auth.accountID)]))
        guard login["type"]?.string == "chatgptAuthTokens" else {
            throw CLIWarmupError.externalAuthNotConfirmed
        }
        let account = try await self.request("account/read", params: .object(["refreshToken": .bool(false)]))["account"]
        guard account?["type"]?.string == "chatgpt",
              account?["email"]?.string?.lowercased() == auth.email else {
            throw CLIWarmupError.identityMismatch
        }
        guard !FileManager.default.fileExists(atPath: self.home.appendingPathComponent("auth.json").path) else {
            throw CLIWarmupError.externalAuthNotConfirmed
        }
    }

    func observe(identity: String) async throws -> WarmupQuotaObservation {
        let result = try await self.request("account/rateLimits/read")
        return try Self.observation(result: result, identity: identity, observedAt: Date())
    }

    static func observation(result: WarmupJSONValue, identity: String, observedAt: Date) throws -> WarmupQuotaObservation {
        guard case .object = result else { throw CLIWarmupError.malformedProtocol }
        var pools = result["rateLimitsByLimitId"]?.object ?? [:]
        if let map = result["rateLimitsByLimitId"], map != .null, map.object == nil {
            throw CLIWarmupError.quotaPoolConflict
        }
        if let primary = result["rateLimits"], primary != .null {
            guard let id = primary["limitId"]?.string, !id.isEmpty else {
                throw CLIWarmupError.quotaPoolConflict
            }
            if let existing = pools[id],
               existing["primary"] != primary["primary"] || existing["secondary"] != primary["secondary"] {
                throw CLIWarmupError.quotaPoolConflict
            }
            pools[id] = primary
        }
        var windows: [WarmupQuotaWindow] = []
        for id in pools.keys.sorted() {
            guard let pool = pools[id], pool.object != nil,
                  pool["limitId"] == nil || pool["limitId"] == .null || pool["limitId"]?.string == id else {
                throw CLIWarmupError.quotaPoolConflict
            }
            for side in ["primary", "secondary"] {
                guard let value = pool[side], value != .null else { continue }
                guard value.object != nil else { throw CLIWarmupError.malformedProtocol }
                let duration = value["windowDurationMins"]?.number
                let used = value["usedPercent"]?.number
                let reset = value["resetsAt"]?.number
                guard duration == nil || (duration!.isFinite && duration! >= 0 && duration!.rounded() == duration!),
                      used == nil || (used!.isFinite && (0...100).contains(used!)),
                      reset == nil || reset!.isFinite else { throw CLIWarmupError.malformedProtocol }
                windows.append(WarmupQuotaWindow(poolID: id, usedPercent: used,
                    windowDurationMinutes: duration.flatMap { Int(exactly: $0) },
                    resetsAt: reset.map { Date(timeIntervalSince1970: $0) }))
            }
        }
        guard !windows.isEmpty else { throw CLIWarmupError.malformedProtocol }
        return WarmupQuotaObservation(accountIdentity: identity, observedAt: observedAt, windows: windows)
    }

    func selectMinimalModel() async throws -> (model: String, effort: String) {
        let response = try await self.request("model/list", params: .object([
            "includeHidden": .bool(false), "limit": .number(100)]))
        guard let models = response["data"]?.array else { throw CLIWarmupError.malformedProtocol }
        for model in models {
            guard let slug = model["model"]?.string, slug.hasSuffix("-luna"),
                  slug.hasPrefix("gpt-"), model["hidden"] != .bool(true) else { continue }
            let efforts = model["supportedReasoningEfforts"]?.array?.compactMap { $0["reasoningEffort"]?.string } ?? []
            guard let effort = ["none", "minimal", "low"].first(where: { efforts.contains($0) }) else { continue }
            return (slug, effort)
        }
        throw CLIWarmupError.minimalModelUnavailable
    }

    func startThread(model: String) async throws -> String {
        let result = try await self.request("thread/start", params: .object([
            "model": .string(model), "modelProvider": .string("openai"), "ephemeral": .bool(true),
            "cwd": .string(self.work.path), "approvalPolicy": .string("never"), "sandbox": .string("read-only"),
            "serviceTier": .string("default"),
            "baseInstructions": .string("Reply exactly OK. Do not use tools, browse, run commands, read files, or delegate."),
            "developerInstructions": .string("This is a one-message acknowledgement. Reply exactly OK.")]))
        guard let threadID = result["thread"]?["id"]?.string, !threadID.isEmpty,
              result["thread"]?["ephemeral"] == .bool(true) else {
            throw CLIWarmupError.ephemeralThreadNotConfirmed
        }
        guard result["modelProvider"]?.string == "openai", result["model"]?.string == model,
              result["sandbox"]?["type"]?.string == "readOnly",
              result["instructionSources"]?.array?.isEmpty == true else {
            throw CLIWarmupError.unsafeThreadConfiguration
        }
        return threadID
    }

    func sendTurn(threadID: String, effort: String) async throws -> String {
        let start = try await self.request("turn/start", params: .object([
            "threadId": .string(threadID), "input": .array([.object([
                "type": .string("text"), "text": .string("只回复 OK")])]),
            "serviceTierForTurn": .string("default"), "effort": .string(effort)]))
        guard let turnID = start["turn"]?["id"]?.string, !turnID.isEmpty else {
            throw CLIWarmupError.malformedProtocol
        }
        try await self.withTimeout(method: "turn/completed", seconds: self.limits.turnTimeout) {
            while true {
                // Completion may precede the turn/start response. Check retained
                // terminal events first, always against both IDs.
                if let index = self.events.firstIndex(where: {
                    $0["method"]?.string == "turn/completed"
                        && $0["params"]?["threadId"]?.string == threadID
                        && $0["params"]?["turn"]?["id"]?.string == turnID
                }) {
                    let event = self.events.remove(at: index)
                    guard event["params"]?["turn"]?["status"]?.string == "completed" else {
                        throw CLIWarmupError.turnFailed
                    }
                    guard let items = event["params"]?["turn"]?["items"]?.array else {
                        throw CLIWarmupError.malformedProtocol
                    }
                    try self.checkItems(items)
                    try self.checkToolEvents(threadID: threadID, turnID: turnID)
                    return
                }
                let message = try await self.nextMessage()
                try self.handleUnsolicited(message)
            }
        }
        return turnID
    }

    func checkToolEvents(threadID: String, turnID: String) throws {
        for event in self.events where event["method"]?.string == "item/completed"
            && event["params"]?["threadId"]?.string == threadID
            && event["params"]?["turnId"]?.string == turnID {
            guard let item = event["params"]?["item"] else { throw CLIWarmupError.malformedProtocol }
            try self.checkItems([item])
        }
    }

    private func checkItems(_ items: [WarmupJSONValue]) throws {
        let allowed = Set(["agentMessage", "userMessage", "reasoning", "plan"])
        for item in items {
            guard let type = item["type"]?.string, allowed.contains(type) else {
                throw CLIWarmupError.unexpectedToolActivity
            }
        }
    }

    private func request(_ method: String, params: WarmupJSONValue = .object([:])) async throws -> WarmupJSONValue {
        try Task.checkCancellation()
        let id = self.nextID
        self.nextID += 1
        try await self.write(.object(["id": .number(Double(id)), "method": .string(method), "params": params]))
        return try await self.withTimeout(method: method, seconds: self.limits.requestTimeout) {
            while true {
                let message = try await self.nextMessage()
                if message["method"] != nil { try self.handleUnsolicited(message); continue }
                guard message["id"]?.number == Double(id) else { continue }
                if let error = message["error"], error != .null {
                    throw CLIWarmupError.rpcRejected(method: method, code: error["code"]?.number.flatMap(Int.init(exactly:)))
                }
                guard let result = message["result"] else { throw CLIWarmupError.malformedProtocol }
                return result
            }
        }
    }

    private func handleUnsolicited(_ message: WarmupJSONValue) throws {
        if message["id"] != nil, message["method"] != nil {
            // Never service refresh-token, approval, or tool requests.
            throw CLIWarmupError.unsupportedServerRequest
        }
        let method = message["method"]?.string
        if method == "turn/completed" || method == "item/completed" {
            guard self.events.count < self.limits.maxQueuedMessages else {
                throw CLIWarmupError.outputLimitExceeded
            }
            self.events.append(message)
        }
    }

    private func nextMessage() async throws -> WarmupJSONValue {
        try Task.checkCancellation()
        if let error = self.readError() { throw error }
        guard let data = try await self.iterator.next() else {
            try Task.checkCancellation()
            if let error = self.readError() { throw error }
            throw CLIWarmupError.serverClosed
        }
        guard let message = try? JSONDecoder().decode(WarmupJSONValue.self, from: data),
              message.object != nil else { throw CLIWarmupError.malformedProtocol }
        return message
    }

    private func withTimeout<T: Sendable>(method: String, seconds: TimeInterval,
        body: @escaping @Sendable () async throws -> T) async throws -> T {
        try await withThrowingTaskGroup(of: T.self) { group in
            group.addTask { try await body() }
            group.addTask {
                try await Task.sleep(for: .seconds(seconds))
                self.interrupt()
                throw CLIWarmupError.timedOut(method)
            }
            defer { group.cancelAll() }
            guard let result = try await group.next() else { throw CLIWarmupError.serverClosed }
            return result
        }
    }

    private func write(_ message: WarmupJSONValue) async throws {
        var data = try JSONEncoder().encode(message)
        data.append(0x0A)
        guard data.count <= 12 * 1024 else { throw CLIWarmupError.outputLimitExceeded }
        let fd = self.input.fileHandleForWriting.fileDescriptor
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(self.limits.requestTimeout))
        var offset = 0
        while offset < data.count {
            try Task.checkCancellation()
            guard clock.now < deadline else { throw CLIWarmupError.timedOut("stdin") }
            let written = data.withUnsafeBytes { bytes in
                Darwin.write(fd, bytes.baseAddress!.advanced(by: offset), data.count - offset)
            }
            if written > 0 { offset += written; continue }
            if written == -1, errno == EINTR { continue }
            guard written == -1, errno == EAGAIN || errno == EWOULDBLOCK else {
                throw CLIWarmupError.serverClosed
            }
            try await Task.sleep(for: .milliseconds(10))
        }
    }

    private func countOutput(_ count: Int) {
        self.readLock.lock()
        self.outputBytes += count
        let exceeded = self.outputBytes > self.limits.maxOutputBytes
        if exceeded { self.terminalReadError = .outputLimitExceeded }
        self.readLock.unlock()
        if exceeded { self.continuation.finish(throwing: CLIWarmupError.outputLimitExceeded); self.interrupt() }
    }

    private func receive(_ data: Data) {
        if data.isEmpty {
            self.output.fileHandleForReading.readabilityHandler = nil
            self.continuation.finish()
            return
        }
        self.countOutput(data.count)
        self.readLock.lock()
        if self.terminalReadError != nil { self.readLock.unlock(); return }
        self.partial.append(data)
        var lines: [Data] = []
        while let newline = self.partial.firstIndex(of: 0x0A) {
            let line = Data(self.partial[..<newline])
            self.partial.removeSubrange(...newline)
            if !line.isEmpty { lines.append(line) }
        }
        let exceeded = self.partial.count > self.limits.maxLineBytes
            || lines.contains(where: { $0.count > self.limits.maxLineBytes })
        if exceeded { self.terminalReadError = .outputLimitExceeded }
        self.readLock.unlock()
        if exceeded { self.continuation.finish(throwing: CLIWarmupError.outputLimitExceeded); self.interrupt(); return }
        for line in lines {
            if case .dropped = self.continuation.yield(line) {
                self.readLock.lock(); self.terminalReadError = .outputLimitExceeded; self.readLock.unlock()
                self.continuation.finish(throwing: CLIWarmupError.outputLimitExceeded)
                self.interrupt()
                return
            }
        }
    }

    private func readError() -> CLIWarmupError? {
        self.readLock.lock(); defer { self.readLock.unlock() }
        return self.terminalReadError
    }

    func interrupt() {
        self.continuation.finish()
        if self.process.isRunning { self.process.terminate() }
    }

    func close() async throws {
        try? self.input.fileHandleForWriting.close()
        if self.process.isRunning { self.process.terminate() }
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: .seconds(2))
        var killed = false
        while self.process.isRunning, clock.now < deadline {
            if !killed, clock.now >= deadline.advanced(by: .seconds(-1.5)) {
                kill(self.process.processIdentifier, SIGKILL)
                killed = true
            }
            // Cancellation must not interrupt reaping and delete a live home.
            await Task.detached { try? await Task.sleep(for: .milliseconds(10)) }.value
        }
        guard !self.process.isRunning else { throw CLIWarmupError.processDidNotExit(pid: self.process.processIdentifier) }
        // isRunning is already false here. Foundation's waitUntilExit may
        // enter a thread-local runloop forever when called from a cooperative
        // executor after a task has been reaped. Verify OS disappearance
        // directly instead of starting a second synchronous wait.
        guard kill(self.process.processIdentifier, 0) != 0, errno == ESRCH else {
            throw CLIWarmupError.processDidNotExit(pid: self.process.processIdentifier)
        }
        WarmupResources.unregister(self.process.processIdentifier)
        self.output.fileHandleForReading.readabilityHandler = nil
        self.errors.fileHandleForReading.readabilityHandler = nil
        self.continuation.finish()
        try? self.output.fileHandleForReading.close()
        try? self.errors.fileHandleForReading.close()
    }
}

private extension WarmupJSONValue {
    var string: String? { if case .string(let value) = self { return value }; return nil }
    var number: Double? { if case .number(let value) = self { return value }; return nil }
    var array: [WarmupJSONValue]? { if case .array(let value) = self { return value }; return nil }
    var object: [String: WarmupJSONValue]? { if case .object(let value) = self { return value }; return nil }
}
