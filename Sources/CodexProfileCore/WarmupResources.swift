import Foundation
import Darwin

public struct WarmupResourceSnapshot: Sendable {
    public let activeCLIs: Int
    public let applicationBytes: UInt64?
    public let cliBytes: UInt64?
    public let peakCLIs: Int
    public let peakCLIBytes: UInt64
}

/// Tracks only children created by this feature. It never counts the user's
/// desktop Codex, other app-server processes, or unrelated system processes.
public enum WarmupResources {
    private static let lock = NSLock()
    private static var children = Set<Int32>()
    private static var peakCount = 0
    private static var peakBytes: UInt64 = 0

    static func register(_ pid: Int32) {
        lock.lock(); defer { lock.unlock() }
        children.insert(pid); peakCount = max(peakCount, children.count)
    }
    static func unregister(_ pid: Int32) {
        lock.lock(); defer { lock.unlock() }
        children.remove(pid)
    }
    public static func snapshot() -> WarmupResourceSnapshot {
        lock.lock(); defer { lock.unlock() }
        let samples = children.map(residentBytes)
        let total: UInt64? = samples.allSatisfy { $0 != nil } ? samples.compactMap { $0 }.reduce(0, +) : nil
        if let total { peakBytes = max(peakBytes, total) }
        return WarmupResourceSnapshot(activeCLIs: children.count,
            applicationBytes: residentBytes(ProcessInfo.processInfo.processIdentifier), cliBytes: total,
            peakCLIs: peakCount, peakCLIBytes: peakBytes)
    }
    private static func residentBytes(_ pid: Int32) -> UInt64? {
        var info = proc_taskinfo()
        let size = MemoryLayout<proc_taskinfo>.size
        guard proc_pidinfo(pid, PROC_PIDTASKINFO, 0, &info, Int32(size)) == Int32(size) else { return nil }
        return info.pti_resident_size
    }
}
