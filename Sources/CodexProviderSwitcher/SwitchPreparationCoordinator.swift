import CodexProviderCore
import Foundation

@MainActor
protocol SwitchPreparationSystem: AnyObject {
    func validateProxyEnvironment() async throws
    func ensureSub2APIAvailable() async throws
    func validateTarget(_ target: Provider) async throws
    func prepareSharedHistoryLaunch() async throws
}

extension CodexSystem: SwitchPreparationSystem {}

@MainActor
struct SwitchPreparationCoordinator {
    let system: any SwitchPreparationSystem

    func prepare(target: Provider) async throws {
        do {
            try await system.validateProxyEnvironment()
        } catch {
            throw StagedSwitchError.wrapping(
                error,
                stage: .proxyCheck
            )
        }
        if target == .sub2api {
            do {
                try await system.ensureSub2APIAvailable()
            } catch {
                throw StagedSwitchError.wrapping(
                    error,
                    stage: .sub2APIRecovery
                )
            }
        }
        do {
            try await system.validateTarget(target)
        } catch {
            throw StagedSwitchError.wrapping(
                error,
                stage: .targetValidation
            )
        }
        do {
            try await system.prepareSharedHistoryLaunch()
        } catch {
            throw StagedSwitchError.wrapping(
                error,
                stage: .sharedHistory
            )
        }
    }
}
