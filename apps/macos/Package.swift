// swift-tools-version: 5.7
// Defines the dependency-free macOS launcher source package.
import PackageDescription

let package = Package(
    name: "SlopTank",
    platforms: [.macOS(.v12)],
    products: [
        .executable(name: "SlopTank", targets: ["SlopTank"])
    ],
    targets: [
        .executableTarget(name: "SlopTank")
    ]
)
