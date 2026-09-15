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
}
