import CodexProviderCore
import Darwin
import Foundation

private let defaultCLIPath =
    "/Applications/ChatGPT.app/Contents/Resources/codex"
private let failureMessage = Data("Codex proxy failed.\n".utf8)
private let chunkSize = 64 * 1024
private let escalationDelay: useconds_t = 500_000
private let forwardedSignals = [SIGINT, SIGTERM, SIGHUP]

private enum ProxyIOError: Error {
    case posix(Int32)
}

private final class FailureState: @unchecked Sendable {
    private let lock = NSLock()
    private var failed = false

    func recordFailure() {
        lock.lock()
        failed = true
        lock.unlock()
    }

    var hasFailed: Bool {
        lock.lock()
        defer { lock.unlock() }
        return failed
    }
}

private final class CompletionCounter: @unchecked Sendable {
    private let condition = NSCondition()
    private var remaining: Int

    init(_ count: Int) {
        remaining = count
    }

    func complete() {
        condition.lock()
        remaining -= 1
        condition.broadcast()
        condition.unlock()
    }

    func wait() {
        condition.lock()
        while remaining > 0 {
            condition.wait()
        }
        condition.unlock()
    }
}

private final class ChildSupervisor: @unchecked Sendable {
    private let condition = NSCondition()
    private let childPID: pid_t
    private var waitSet: sigset_t
    private var rawStatus: Int32?
    private var terminationRequested = false
    private var signalThread: Thread?
    private var waitThread: Thread?

    init(childPID: pid_t, waitSet: sigset_t) {
        self.childPID = childPID
        self.waitSet = waitSet
    }

    func start() {
        let waiter = Thread { [self] in
            var blockedSignals = waitSet
            _ = pthread_sigmask(SIG_BLOCK, &blockedSignals, nil)
            reapBlocking()
        }
        waiter.name = "Codex proxy child waiter"
        waitThread = waiter
        waiter.start()

        let signaler = Thread { [self] in
            var blockedSignals = waitSet
            _ = pthread_sigmask(SIG_BLOCK, &blockedSignals, nil)
            #if PROXY_TESTING
            _ = createTestMarker(
                environmentName:
                    "CODEX_PROVIDER_PROXY_TEST_SUPERVISOR_READY_FILE"
            )
            #endif
            supervise()
        }
        signaler.name = "Codex proxy signal supervisor"
        signalThread = signaler
        signaler.start()
    }

    func waitForExit() -> Int32 {
        condition.lock()
        while rawStatus == nil {
            condition.wait()
        }
        let status = rawStatus!
        condition.unlock()
        return status
    }

    func terminateAfterProxyFailure() {
        condition.lock()
        guard rawStatus == nil, !terminationRequested else {
            condition.unlock()
            return
        }
        terminationRequested = true
        condition.unlock()

        _ = Darwin.kill(childPID, SIGTERM)
        usleep(escalationDelay)

        condition.lock()
        let childIsRunning = rawStatus == nil
        condition.unlock()
        if childIsRunning {
            _ = Darwin.kill(childPID, SIGKILL)
        }
    }

    private func supervise() {
        while true {
            var signalNumber: Int32 = 0
            let result = sigwait(&waitSet, &signalNumber)
            guard result == 0 else {
                _ = Darwin.kill(childPID, SIGKILL)
                return
            }

            if hasExited || childExitedWithoutReaping() {
                terminateWrapper(with: signalNumber)
            }

            if Darwin.kill(childPID, signalNumber) != 0, errno == ESRCH {
                if hasExited || childExitedWithoutReaping() {
                    terminateWrapper(with: signalNumber)
                }
            }
        }
    }

    private var hasExited: Bool {
        condition.lock()
        defer { condition.unlock() }
        return rawStatus != nil
    }

    private func childExitedWithoutReaping() -> Bool {
        var info = siginfo_t()
        let result = Darwin.waitid(
            P_PID,
            UInt32(childPID),
            &info,
            WEXITED | WNOHANG | WNOWAIT
        )
        if result != 0, errno == ECHILD {
            return true
        }
        return result == 0 && info.si_pid == childPID
    }

    private func reapBlocking() {
        var status: Int32 = 0
        while true {
            let result = Darwin.waitpid(childPID, &status, 0)
            if result == childPID {
                publish(status)
                return
            }
            if result == -1, errno == EINTR {
                continue
            }
            publish(1 << 8)
            return
        }
    }

    private func publish(_ status: Int32) {
        condition.lock()
        if rawStatus == nil {
            rawStatus = status
            condition.broadcast()
        }
        condition.unlock()
    }
}

private struct PipeDescriptors {
    let read: Int32
    let write: Int32
}

private struct SpawnedChild {
    let pid: pid_t
    let input: Int32
    let output: Int32
    let error: Int32
}

private func reportFailure() {
    try? writeAll(failureMessage, to: STDERR_FILENO)
}

private func exitWithFailure() -> Never {
    reportFailure()
    Darwin.exit(EXIT_FAILURE)
}

private func terminateWrapper(with signalNumber: Int32) -> Never {
    Darwin.signal(signalNumber, SIG_DFL)
    var signalSet = sigset_t()
    sigemptyset(&signalSet)
    sigaddset(&signalSet, signalNumber)
    _ = pthread_sigmask(SIG_UNBLOCK, &signalSet, nil)
    _ = pthread_kill(pthread_self(), signalNumber)
    Darwin._exit(128 + signalNumber)
}

private func makeSignalSet() -> sigset_t {
    var signalSet = sigset_t()
    sigemptyset(&signalSet)
    for signalNumber in forwardedSignals {
        sigaddset(&signalSet, signalNumber)
    }
    return signalSet
}

private func rewritten(_ line: Data) -> Data {
    guard let text = String(data: line, encoding: .utf8) else {
        return line
    }
    return Data(AppServerRequestRewriter.rewrite(line: text).utf8)
}

private func readChunk(from descriptor: Int32) throws -> Data? {
    var bytes = [UInt8](repeating: 0, count: chunkSize)
    while true {
        let count = bytes.withUnsafeMutableBytes { buffer in
            Darwin.read(descriptor, buffer.baseAddress, buffer.count)
        }
        if count > 0 {
            return Data(bytes.prefix(count))
        }
        if count == 0 {
            return nil
        }
        if errno != EINTR {
            throw ProxyIOError.posix(errno)
        }
    }
}

private func writeAll(_ data: Data, to descriptor: Int32) throws {
    try data.withUnsafeBytes { buffer in
        var offset = 0
        while offset < buffer.count {
            let count = Darwin.write(
                descriptor,
                buffer.baseAddress?.advanced(by: offset),
                buffer.count - offset
            )
            if count > 0 {
                offset += count
                continue
            }
            if count == -1, errno == EINTR {
                continue
            }
            throw ProxyIOError.posix(errno)
        }
    }
}

private func pumpOutput(
    from source: Int32,
    to destination: Int32,
    supervisor: ChildSupervisor,
    failureState: FailureState
) {
    var failed = false
    do {
        while let data = try readChunk(from: source) {
            try writeAll(data, to: destination)
        }
    } catch {
        failed = true
        failureState.recordFailure()
    }

    _ = Darwin.close(source)
    if failed {
        supervisor.terminateAfterProxyFailure()
    }
}

private func pumpInput(
    to childInput: Int32,
    supervisor: ChildSupervisor,
    failureState: FailureState
) {
    var failure: ProxyIOError?
    do {
        var lineBuffer = AppServerLineBuffer()
        while let data = try readChunk(from: STDIN_FILENO) {
            for line in lineBuffer.append(data) {
                var output = rewritten(line)
                output.append(0x0A)
                try writeAll(output, to: childInput)
            }
        }

        if let partialLine = lineBuffer.finish() {
            try writeAll(rewritten(partialLine), to: childInput)
        }
    } catch let error as ProxyIOError {
        failure = error
    } catch {
        failure = .posix(EIO)
    }

    _ = Darwin.close(childInput)
    if case let .posix(errorNumber) = failure, errorNumber != EPIPE {
        failureState.recordFailure()
        supervisor.terminateAfterProxyFailure()
    }
}

private func createPipe() -> PipeDescriptors? {
    var descriptors = [Int32](repeating: -1, count: 2)
    let result = descriptors.withUnsafeMutableBufferPointer { buffer in
        Darwin.pipe(buffer.baseAddress!)
    }
    guard result == 0 else {
        return nil
    }
    return PipeDescriptors(read: descriptors[0], write: descriptors[1])
}

private func close(_ pipes: [PipeDescriptors]) {
    for pipe in pipes {
        _ = Darwin.close(pipe.read)
        _ = Darwin.close(pipe.write)
    }
}

private func withCStringArray<Result>(
    _ strings: [String],
    body: (
        UnsafeMutablePointer<UnsafeMutablePointer<CChar>?>
    ) -> Result
) -> Result? {
    let duplicated = strings.map { strdup($0) }
    guard !duplicated.contains(where: { $0 == nil }) else {
        duplicated.forEach { free($0) }
        return nil
    }
    defer { duplicated.forEach { free($0) } }

    var pointers = duplicated + [nil]
    return pointers.withUnsafeMutableBufferPointer { buffer in
        body(buffer.baseAddress!)
    }
}

private func spawnChild(
    realCLIPath: String,
    signalSet: sigset_t
) -> SpawnedChild? {
    guard let input = createPipe() else {
        return nil
    }
    guard let output = createPipe() else {
        close([input])
        return nil
    }
    guard let error = createPipe() else {
        close([input, output])
        return nil
    }
    let pipes = [input, output, error]

    var actions: posix_spawn_file_actions_t? = nil
    guard posix_spawn_file_actions_init(&actions) == 0 else {
        close(pipes)
        return nil
    }
    defer { posix_spawn_file_actions_destroy(&actions) }

    let actionResults = [
        posix_spawn_file_actions_adddup2(
            &actions,
            input.read,
            STDIN_FILENO
        ),
        posix_spawn_file_actions_adddup2(
            &actions,
            output.write,
            STDOUT_FILENO
        ),
        posix_spawn_file_actions_adddup2(
            &actions,
            error.write,
            STDERR_FILENO
        ),
    ] + pipes.flatMap { pipe in
        [
            posix_spawn_file_actions_addclose(&actions, pipe.read),
            posix_spawn_file_actions_addclose(&actions, pipe.write),
        ]
    }
    guard actionResults.allSatisfy({ $0 == 0 }) else {
        close(pipes)
        return nil
    }

    var attributes: posix_spawnattr_t? = nil
    guard posix_spawnattr_init(&attributes) == 0 else {
        close(pipes)
        return nil
    }
    defer { posix_spawnattr_destroy(&attributes) }

    var emptyMask = sigset_t()
    sigemptyset(&emptyMask)
    var defaultSignals = signalSet
    sigaddset(&defaultSignals, SIGPIPE)
    let flags = Int16(POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSIGDEF)
    guard posix_spawnattr_setsigmask(&attributes, &emptyMask) == 0,
          posix_spawnattr_setsigdefault(&attributes, &defaultSignals) == 0,
          posix_spawnattr_setflags(&attributes, flags) == 0
    else {
        close(pipes)
        return nil
    }

    let arguments = [realCLIPath] + CommandLine.arguments.dropFirst()
    var childPID: pid_t = 0
    let spawnResult = realCLIPath.withCString { path in
        withCStringArray(arguments) { argumentPointers in
            posix_spawn(
                &childPID,
                path,
                &actions,
                &attributes,
                argumentPointers,
                environ
            )
        }
    }
    guard spawnResult == 0 else {
        close(pipes)
        return nil
    }

    _ = Darwin.close(input.read)
    _ = Darwin.close(output.write)
    _ = Darwin.close(error.write)
    return SpawnedChild(
        pid: childPID,
        input: input.write,
        output: output.read,
        error: error.read
    )
}

#if PROXY_TESTING
private func createTestMarker(environmentName: String) -> Bool {
    guard let pathValue = getenv(environmentName) else {
        return true
    }
    let descriptor = Darwin.open(
        String(cString: pathValue),
        O_WRONLY | O_CREAT | O_EXCL,
        S_IRUSR | S_IWUSR
    )
    guard descriptor >= 0 else {
        return false
    }
    _ = Darwin.close(descriptor)
    return true
}

private func performPreSpawnTestHook() -> Bool {
    guard getenv("CODEX_PROVIDER_PROXY_TEST_READY_FILE") != nil,
          let delayValue = getenv(
        "CODEX_PROVIDER_PROXY_TEST_DELAY_USEC"
    ), let delay = useconds_t(String(cString: delayValue))
    else {
        return true
    }

    guard createTestMarker(
        environmentName: "CODEX_PROVIDER_PROXY_TEST_READY_FILE"
    ) else {
        return false
    }
    usleep(delay)
    return true
}
#endif

private func runAppServer(realCLIPath: String) -> Never {
    var signalSet = makeSignalSet()
    guard pthread_sigmask(SIG_BLOCK, &signalSet, nil) == 0 else {
        exitWithFailure()
    }
    for signalNumber in forwardedSignals {
        Darwin.signal(signalNumber, SIG_DFL)
    }

    #if PROXY_TESTING
    guard performPreSpawnTestHook() else {
        exitWithFailure()
    }
    #endif

    guard let child = spawnChild(
        realCLIPath: realCLIPath,
        signalSet: signalSet
    ) else {
        exitWithFailure()
    }

    Darwin.signal(SIGPIPE, SIG_IGN)
    let supervisor = ChildSupervisor(
        childPID: child.pid,
        waitSet: signalSet
    )
    supervisor.start()

    let failureState = FailureState()
    let outputCompletion = CompletionCounter(2)

    let outputThread = Thread {
        pumpOutput(
            from: child.output,
            to: STDOUT_FILENO,
            supervisor: supervisor,
            failureState: failureState
        )
        outputCompletion.complete()
    }
    outputThread.name = "Codex proxy stdout pump"
    outputThread.start()

    let errorThread = Thread {
        pumpOutput(
            from: child.error,
            to: STDERR_FILENO,
            supervisor: supervisor,
            failureState: failureState
        )
        outputCompletion.complete()
    }
    errorThread.name = "Codex proxy stderr pump"
    errorThread.start()

    let inputThread = Thread {
        pumpInput(
            to: child.input,
            supervisor: supervisor,
            failureState: failureState
        )
    }
    inputThread.name = "Codex proxy stdin pump"
    inputThread.start()

    let rawStatus = supervisor.waitForExit()
    outputCompletion.wait()

    if failureState.hasFailed {
        exitWithFailure()
    }

    let terminationSignal = rawStatus & 0x7F
    if terminationSignal == 0 {
        Darwin.exit((rawStatus >> 8) & 0xFF)
    }
    terminateWrapper(with: terminationSignal)
}

private let realCLIPath: String = {
    guard let override = getenv("CODEX_PROVIDER_REAL_CLI") else {
        return defaultCLIPath
    }
    return String(cString: override)
}()

guard Darwin.access(realCLIPath, X_OK) == 0 else {
    exitWithFailure()
}

guard CommandLine.arguments.dropFirst().first == "app-server" else {
    realCLIPath.withCString { path in
        _ = Darwin.execv(path, CommandLine.unsafeArgv)
    }
    exitWithFailure()
}

runAppServer(realCLIPath: realCLIPath)
