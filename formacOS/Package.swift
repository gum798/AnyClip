// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "AnyClip",
    platforms: [.macOS(.v14)],
    dependencies: [
        // Explicit swift-testing dependency is REQUIRED on CLT-only machines (no
        // Xcode): the toolchain-bundled Testing.framework builds but silently runs
        // zero tests under `swift test` (exit 0 even on failures). The OSS package
        // restores correct discovery + non-zero exit on failure. Verified 2026-06-11.
        // Do not remove without re-running the failing-test experiment.
        .package(url: "https://github.com/swiftlang/swift-testing.git", from: "0.10.0"),
    ],
    targets: [
        .target(name: "AnyClipCore"),
        .target(
            name: "AnyClipDaemon",
            dependencies: ["AnyClipCore"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .executableTarget(
            name: "AnyClipApp",
            dependencies: ["AnyClipCore", "AnyClipDaemon"],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
        .testTarget(
            name: "AnyClipCoreTests",
            dependencies: [
                "AnyClipCore",
                .product(name: "Testing", package: "swift-testing"),
            ],
            resources: [.copy("Fixtures")]
        ),
        .testTarget(
            name: "AnyClipDaemonTests",
            dependencies: [
                "AnyClipDaemon",
                .product(name: "Testing", package: "swift-testing"),
            ],
            swiftSettings: [.swiftLanguageMode(.v5)]
        ),
    ]
)
