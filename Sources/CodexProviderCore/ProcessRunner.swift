import Darwin
import Foundation

public struct ProcessResult: Sendable {
    public let exitCode: Int32
    public let standardOutput: String

    public init(exitCode: Int32, standardOutput: String) {
        self.exitCode = exitCode
        self.standardOutput = standardOutput
    }
}

public enum ProcessRunnerError: Error, Equatable {
    case timedOut
}

public enum ProcessRunner {
    public static func run(
        executable: URL,
        arguments: [String],
        captureOutput: Bool,
        timeout: TimeInterval = 30
    ) throws -> ProcessResult {
        let process = Process()
        process.executableURL = executable
        process.arguments = arguments
        process.standardError = FileHandle.nullDevice

        let outputPipe = captureOutput ? Pipe() : nil
        process.standardOutput = outputPipe ?? FileHandle.nullDevice

        let outputDescriptor = outputPipe?.fileHandleForReading.fileDescriptor
        if let outputDescriptor {
            let flags = fcntl(outputDescriptor, F_GETFL)
            guard flags >= 0,
                  fcntl(outputDescriptor, F_SETFL, flags | O_NONBLOCK) >= 0
            else {
                throw POSIXError(.EIO)
            }
        }
        defer {
            try? outputPipe?.fileHandleForReading.close()
            try? outputPipe?.fileHandleForWriting.close()
        }

        try process.run()
        try? outputPipe?.fileHandleForWriting.close()

        var outputData = Data()
        let deadline = Date().addingTimeInterval(max(timeout, 0))
        while process.isRunning && Date() < deadline {
            if let outputDescriptor {
                try drainAvailableOutput(
                    from: outputDescriptor,
                    into: &outputData
                )
            }
            Thread.sleep(forTimeInterval: 0.01)
        }

        let timedOut = process.isRunning
        if timedOut {
            process.terminate()
            let terminationDeadline = Date().addingTimeInterval(0.5)
            while process.isRunning && Date() < terminationDeadline {
                if let outputDescriptor {
                    try drainAvailableOutput(
                        from: outputDescriptor,
                        into: &outputData
                    )
                }
                Thread.sleep(forTimeInterval: 0.01)
            }
            if process.isRunning {
                kill(process.processIdentifier, SIGKILL)
            }
        }

        if let outputDescriptor {
            try drainAvailableOutput(from: outputDescriptor, into: &outputData)
        }

        if timedOut {
            throw ProcessRunnerError.timedOut
        }

        return ProcessResult(
            exitCode: process.terminationStatus,
            standardOutput: String(data: outputData, encoding: .utf8) ?? ""
        )
    }

    private static func drainAvailableOutput(
        from descriptor: Int32,
        into output: inout Data
    ) throws {
        var buffer = [UInt8](repeating: 0, count: 16_384)
        while true {
            let count = buffer.withUnsafeMutableBytes { bytes in
                read(descriptor, bytes.baseAddress, bytes.count)
            }
            if count > 0 {
                output.append(contentsOf: buffer.prefix(Int(count)))
                continue
            }
            if count == 0 {
                return
            }
            if errno == EINTR {
                continue
            }
            if errno == EAGAIN || errno == EWOULDBLOCK {
                return
            }
            throw POSIXError(
                POSIXErrorCode(rawValue: errno) ?? .EIO
            )
        }
    }
}
