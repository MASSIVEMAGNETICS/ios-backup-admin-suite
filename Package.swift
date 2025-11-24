// swift-tools-version: 5.7
// The swift-tools-version declares the minimum version of Swift required to build this package.

import PackageDescription

let package = Package(
    name: "IOSBackupAdminSuite",
    platforms: [
        .iOS(.v15),
        .macOS(.v12)
    ],
    products: [
        // Library for iOS backup functionality
        .library(
            name: "IOSBackupKit",
            targets: ["IOSBackupKit"]
        ),
        // Command-line tool for restoring backups
        .executable(
            name: "restore-tool",
            targets: ["RestoreTool"]
        ),
        // Example demonstrating backup/restore
        .executable(
            name: "example",
            targets: ["Example"]
        ),
        // Windows Desktop Application
        .executable(
            name: "ios-backup-windows",
            targets: ["WindowsApp"]
        )
    ],
    dependencies: [
        .package(url: "https://github.com/apple/swift-crypto.git", from: "3.0.0")
    ],
    targets: [
        // Core backup library
        .target(
            name: "IOSBackupKit",
            dependencies: [
                .product(name: "Crypto", package: "swift-crypto")
            ],
            path: "Sources/IOSBackupKit"
        ),
        // Restore command-line tool
        .executableTarget(
            name: "RestoreTool",
            dependencies: ["IOSBackupKit"],
            path: "Sources/RestoreTool"
        ),
        // Example executable
        .executableTarget(
            name: "Example",
            dependencies: ["IOSBackupKit"],
            path: "Examples"
        ),
        // Windows Desktop Application
        .executableTarget(
            name: "WindowsApp",
            dependencies: ["IOSBackupKit"],
            path: "Sources/WindowsApp"
        ),
        // Tests
        .testTarget(
            name: "IOSBackupKitTests",
            dependencies: ["IOSBackupKit"],
            path: "Tests"
        )
    ]
)
