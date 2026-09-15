// swift-tools-version: 5.10

import PackageDescription

let package = Package(
    name: "CodexSwitch",
    platforms: [
        .macOS(.v14),
    ],
    products: [
        .library(name: "CodexProviderCore", targets: ["CodexProviderCore"]),
        .library(name: "CodexProfileCore", targets: ["CodexProfileCore"]),
        .executable(
            name: "CodexProviderSwitcher",
            targets: ["CodexProviderSwitcher"]
        ),
        .executable(
            name: "codex-profile",
            targets: ["CodexProfileCLI"]
        ),
        .executable(
            name: "CodexSharedHistoryProxy",
            targets: ["CodexSharedHistoryProxy"]
        ),
    ],
    targets: [
        .target(name: "CodexProviderCore"),
        .target(name: "CodexProfileCore"),
        .executableTarget(
            name: "CodexProviderSwitcher",
            dependencies: ["CodexProviderCore", "CodexProfileCore"],
            linkerSettings: [
                .linkedFramework("Cocoa"),
                .linkedFramework("SwiftUI"),
                .linkedFramework("Security"),
            ]
        ),
        .executableTarget(
            name: "CodexProfileCLI",
            dependencies: ["CodexProfileCore"]
        ),
        .executableTarget(
            name: "CodexSharedHistoryProxy",
            dependencies: ["CodexProviderCore"],
            swiftSettings: [
                .define(
                    "PROXY_TESTING",
                    .when(configuration: .debug)
                ),
            ]
        ),
        .testTarget(
            name: "CodexProviderCoreTests",
            dependencies: ["CodexProviderCore"]
        ),
        .testTarget(
            name: "CodexProviderSwitcherTests",
            dependencies: ["CodexProviderSwitcher", "CodexProviderCore"]
        ),
        .testTarget(name: "CodexSharedHistoryProxyTests"),
        .testTarget(
            name: "AuthBlobTests",
            dependencies: ["CodexProfileCore"]
        ),
        .testTarget(
            name: "ProfileSelectorTests",
            dependencies: ["CodexProfileCore"]
        ),
        .testTarget(
            name: "TokenRenewalTests",
            dependencies: ["CodexProfileCore"]
        ),
    ]
)
