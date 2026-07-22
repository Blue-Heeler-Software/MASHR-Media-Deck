// swift-tools-version: 6.0
import PackageDescription

let package = Package(
    name: "MashrMediaDeckMac",
    platforms: [.macOS(.v13)],
    products: [
        .executable(name: "mashr-media-deck-mac", targets: ["MashrMediaDeckMac"])
    ],
    targets: [
        .executableTarget(name: "MashrMediaDeckMac")
    ]
)

