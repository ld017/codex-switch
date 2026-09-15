import AppKit
import Darwin
import Foundation

@MainActor
protocol CodexProcessControlling {
    func runningPIDs() -> Set<pid_t>
    func requestNativeForceTermination(for PIDs: Set<pid_t>)
    func sendSignal(_ signal: Int32, to PID: pid_t)
}

@MainActor
struct AppKitCodexProcessController: CodexProcessControlling {
    let bundleIdentifier: String

    func runningPIDs() -> Set<pid_t> {
        Set(
            NSRunningApplication.runningApplications(
                withBundleIdentifier: bundleIdentifier
            ).map(\.processIdentifier)
        )
    }

    func requestNativeForceTermination(for PIDs: Set<pid_t>) {
        NSRunningApplication.runningApplications(
            withBundleIdentifier: bundleIdentifier
        )
        .filter { PIDs.contains($0.processIdentifier) }
        .forEach { _ = $0.forceTerminate() }
    }

    func sendSignal(_ signal: Int32, to PID: pid_t) {
        _ = Darwin.kill(PID, signal)
    }
}

@MainActor
struct CodexProcessTerminator {
    let controller: any CodexProcessControlling
    let nativeWaitAttempts: Int
    let fallbackWaitAttempts: Int
    let waitBetweenChecks: () async -> Void

    init(
        controller: any CodexProcessControlling =
            AppKitCodexProcessController(
                bundleIdentifier: CodexSystem.chatGPTBundleIdentifier
            ),
        nativeWaitAttempts: Int = 12,
        fallbackWaitAttempts: Int = 20,
        waitBetweenChecks: @escaping () async -> Void = {
            try? await Task.sleep(nanoseconds: 250_000_000)
        }
    ) {
        self.controller = controller
        self.nativeWaitAttempts = nativeWaitAttempts
        self.fallbackWaitAttempts = fallbackWaitAttempts
        self.waitBetweenChecks = waitBetweenChecks
    }

    func forceTerminateCodex() async -> Bool {
        let capturedPIDs = controller.runningPIDs()
        guard !capturedPIDs.isEmpty else {
            return true
        }

        controller.requestNativeForceTermination(for: capturedPIDs)
        if await waitUntilCapturedPIDsExit(
            capturedPIDs,
            attempts: nativeWaitAttempts
        ) {
            return true
        }

        let remainingPIDs = capturedPIDs.intersection(
            controller.runningPIDs()
        )
        remainingPIDs.sorted().forEach {
            controller.sendSignal(SIGKILL, to: $0)
        }
        return await waitUntilCapturedPIDsExit(
            capturedPIDs,
            attempts: fallbackWaitAttempts
        )
    }

    private func waitUntilCapturedPIDsExit(
        _ capturedPIDs: Set<pid_t>,
        attempts: Int
    ) async -> Bool {
        let checkCount = max(attempts, 1)
        for attempt in 0..<checkCount {
            if capturedPIDs.isDisjoint(with: controller.runningPIDs()) {
                return true
            }
            if attempt + 1 < checkCount {
                await waitBetweenChecks()
            }
        }
        return false
    }
}
