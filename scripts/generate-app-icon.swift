import AppKit
import Foundation

private let fileManager = FileManager.default
private let outputURL = URL(
    fileURLWithPath: CommandLine.arguments.dropFirst().first
        ?? "Resources/AppIcon.icns"
)
private let temporaryRoot = fileManager.temporaryDirectory
    .appendingPathComponent("codex-provider-icon-\(UUID().uuidString)")
private let iconsetURL = temporaryRoot.appendingPathComponent("AppIcon.iconset")

private struct Representation {
    let filename: String
    let pixels: Int
}

private let representations = [
    Representation(filename: "icon_16x16.png", pixels: 16),
    Representation(filename: "icon_16x16@2x.png", pixels: 32),
    Representation(filename: "icon_32x32.png", pixels: 32),
    Representation(filename: "icon_32x32@2x.png", pixels: 64),
    Representation(filename: "icon_128x128.png", pixels: 128),
    Representation(filename: "icon_128x128@2x.png", pixels: 256),
    Representation(filename: "icon_256x256.png", pixels: 256),
    Representation(filename: "icon_256x256@2x.png", pixels: 512),
    Representation(filename: "icon_512x512.png", pixels: 512),
    Representation(filename: "icon_512x512@2x.png", pixels: 1024),
]

private func drawArrow(
    size: CGFloat,
    color: NSColor,
    start: NSPoint,
    control1: NSPoint,
    control2: NSPoint,
    end: NSPoint,
    arrowPoints: [NSPoint]
) {
    let stroke = NSBezierPath()
    stroke.move(to: start)
    stroke.curve(to: end, controlPoint1: control1, controlPoint2: control2)
    stroke.lineWidth = size * 0.082
    stroke.lineCapStyle = .round
    stroke.lineJoinStyle = .round
    color.setStroke()
    stroke.stroke()

    let arrow = NSBezierPath()
    arrow.move(to: arrowPoints[0])
    arrowPoints.dropFirst().forEach { arrow.line(to: $0) }
    arrow.close()
    color.setFill()
    arrow.fill()
}

private func renderIcon(pixels: Int, to url: URL) throws {
    let size = CGFloat(pixels)
    guard let bitmap = NSBitmapImageRep(
        bitmapDataPlanes: nil,
        pixelsWide: pixels,
        pixelsHigh: pixels,
        bitsPerSample: 8,
        samplesPerPixel: 4,
        hasAlpha: true,
        isPlanar: false,
        colorSpaceName: .deviceRGB,
        bytesPerRow: 0,
        bitsPerPixel: 0
    ), let context = NSGraphicsContext(bitmapImageRep: bitmap) else {
        throw CocoaError(.fileWriteUnknown)
    }

    NSGraphicsContext.saveGraphicsState()
    NSGraphicsContext.current = context
    context.imageInterpolation = .high
    NSColor.clear.setFill()
    NSRect(x: 0, y: 0, width: size, height: size).fill()

    let tileRect = NSRect(
        x: size * 0.065,
        y: size * 0.065,
        width: size * 0.87,
        height: size * 0.87
    )
    let tile = NSBezierPath(
        roundedRect: tileRect,
        xRadius: size * 0.20,
        yRadius: size * 0.20
    )
    NSColor(
        calibratedRed: 0.105,
        green: 0.122,
        blue: 0.135,
        alpha: 1
    ).setFill()
    tile.fill()
    NSColor.white.withAlphaComponent(0.12).setStroke()
    tile.lineWidth = max(1, size * 0.012)
    tile.stroke()

    let cyan = NSColor(
        calibratedRed: 0.16,
        green: 0.78,
        blue: 0.82,
        alpha: 1
    )
    drawArrow(
        size: size,
        color: cyan,
        start: NSPoint(x: size * 0.25, y: size * 0.54),
        control1: NSPoint(x: size * 0.36, y: size * 0.72),
        control2: NSPoint(x: size * 0.60, y: size * 0.74),
        end: NSPoint(x: size * 0.72, y: size * 0.60),
        arrowPoints: [
            NSPoint(x: size * 0.79, y: size * 0.55),
            NSPoint(x: size * 0.63, y: size * 0.54),
            NSPoint(x: size * 0.72, y: size * 0.69),
        ]
    )

    let coral = NSColor(
        calibratedRed: 0.98,
        green: 0.39,
        blue: 0.34,
        alpha: 1
    )
    drawArrow(
        size: size,
        color: coral,
        start: NSPoint(x: size * 0.75, y: size * 0.46),
        control1: NSPoint(x: size * 0.64, y: size * 0.28),
        control2: NSPoint(x: size * 0.40, y: size * 0.26),
        end: NSPoint(x: size * 0.28, y: size * 0.40),
        arrowPoints: [
            NSPoint(x: size * 0.21, y: size * 0.45),
            NSPoint(x: size * 0.37, y: size * 0.46),
            NSPoint(x: size * 0.28, y: size * 0.31),
        ]
    )

    NSGraphicsContext.restoreGraphicsState()
    guard let png = bitmap.representation(using: .png, properties: [:]) else {
        throw CocoaError(.fileWriteUnknown)
    }
    try png.write(to: url, options: .atomic)
}

try fileManager.createDirectory(
    at: iconsetURL,
    withIntermediateDirectories: true
)
defer {
    try? fileManager.removeItem(at: temporaryRoot)
}

for representation in representations {
    try renderIcon(
        pixels: representation.pixels,
        to: iconsetURL.appendingPathComponent(representation.filename)
    )
}

try fileManager.createDirectory(
    at: outputURL.deletingLastPathComponent(),
    withIntermediateDirectories: true
)
let iconutil = Process()
iconutil.executableURL = URL(fileURLWithPath: "/usr/bin/iconutil")
iconutil.arguments = [
    "-c", "icns",
    iconsetURL.path,
    "-o", outputURL.path,
]
try iconutil.run()
iconutil.waitUntilExit()
guard iconutil.terminationStatus == 0 else {
    throw CocoaError(.fileWriteUnknown)
}
