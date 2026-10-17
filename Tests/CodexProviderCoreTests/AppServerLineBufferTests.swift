import Foundation
import XCTest
@testable import CodexProviderCore

final class AppServerLineBufferTests: XCTestCase {
    func testEmitsLineSplitAcrossArbitraryChunks() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(buffer.append(Data("hel".utf8)), [])
        XCTAssertEqual(buffer.append(Data("lo\n".utf8)), [Data("hello".utf8)])
        XCTAssertNil(buffer.finish())
    }

    func testEmitsMultipleCompleteLinesFromOneChunk() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(
            buffer.append(Data("first\nsecond\n".utf8)),
            [Data("first".utf8), Data("second".utf8)]
        )
        XCTAssertNil(buffer.finish())
    }

    func testRemovesTerminalCarriageReturnFromCRLFLines() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(
            buffer.append(Data("first\r\nsecond\r\n".utf8)),
            [Data("first".utf8), Data("second".utf8)]
        )
    }

    func testPreservesBlankLines() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(
            buffer.append(Data("\n\n".utf8)),
            [Data(), Data()]
        )
    }

    func testFinishReturnsTrailingPartialDataOnlyOnce() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(buffer.append(Data("partial".utf8)), [])
        XCTAssertEqual(buffer.finish(), Data("partial".utf8))
        XCTAssertNil(buffer.finish())
    }

    func testEmitsManyShortLinesWithoutChangingOrder() {
        var buffer = AppServerLineBuffer()
        let input = Data(repeating: 0x0A, count: 10_000)

        XCTAssertEqual(
            buffer.append(input),
            Array(repeating: Data(), count: 10_000)
        )
        XCTAssertNil(buffer.finish())
    }

    func testPreservesSplitUTF8AndCRLFAcrossEveryChunkBoundary() {
        let input = Data("中文🙂\r\nsecond\r\n尾行".utf8)
        for boundary in 0...input.count {
            var buffer = AppServerLineBuffer()
            let first = buffer.append(Data(input.prefix(boundary)))
            let second = buffer.append(Data(input.dropFirst(boundary)))

            XCTAssertEqual(first + second, [Data("中文🙂".utf8), Data("second".utf8)])
            XCTAssertEqual(buffer.finish(), Data("尾行".utf8))
        }
    }

    func testResumesScanningAfterACompleteLineAndRetainedPartialLine() {
        var buffer = AppServerLineBuffer()

        XCTAssertEqual(buffer.append(Data("first\npar".utf8)), [Data("first".utf8)])
        XCTAssertEqual(buffer.append(Data()), [])
        XCTAssertEqual(buffer.append(Data("tial\nnext".utf8)), [Data("partial".utf8)])
        XCTAssertEqual(buffer.append(Data("\r".utf8)), [])
        XCTAssertEqual(buffer.append(Data("\n".utf8)), [Data("next".utf8)])
        XCTAssertNil(buffer.finish())
    }

    func testCanBeReusedAfterFinishingAPartialLine() {
        var buffer = AppServerLineBuffer()
        XCTAssertEqual(buffer.append(Data("old partial".utf8)), [])
        XCTAssertEqual(buffer.finish(), Data("old partial".utf8))

        XCTAssertEqual(buffer.append(Data("new\n".utf8)), [Data("new".utf8)])
        XCTAssertNil(buffer.finish())
    }

    func testPreservesAnUnboundedLargeLineAcrossSmallChunks() {
        var buffer = AppServerLineBuffer()
        let line = Data(repeating: 0x61, count: 4 * 1024 * 1024)
        let chunkSize = 4093
        for offset in stride(from: 0, to: line.count, by: chunkSize) {
            let end = min(offset + chunkSize, line.count)
            XCTAssertEqual(buffer.append(Data(line[offset..<end])), [])
        }

        XCTAssertEqual(buffer.append(Data([0x0A])), [line])
        XCTAssertNil(buffer.finish())
    }
}
