// Map the launcher architecture to the matching self-contained runtime.
import Foundation
func macOSRuntimeID(architecture: String) throws -> String {
    switch architecture {
    case "x86_64": return "osx-x64"
    case "arm64": return "osx-arm64"
    default:
        throw NSError(domain: "org.sloptank.desktop.launcher", code: 1019,
                      userInfo: [NSLocalizedDescriptionKey: "Unsupported Mac architecture: \(architecture)"])
    }
}

func currentMacOSRuntimeID() throws -> String {
    #if arch(x86_64)
    return try macOSRuntimeID(architecture: "x86_64")
    #elseif arch(arm64)
    return try macOSRuntimeID(architecture: "arm64")
    #else
    return try macOSRuntimeID(architecture: "unsupported")
    #endif
}
