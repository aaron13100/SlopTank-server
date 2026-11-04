// Seeds an available first-run port, then reads the server's saved network policy.
import Darwin
import Foundation

struct NetworkSettings {
    let baseURL: URL

    /// Creates only a new profile's network settings. Existing settings remain authoritative.
    static func prepareFirstRun(at file: URL, preferredPort: Int = 8096) throws {
        var info = stat()
        if lstat(file.path, &info) == 0 { return }
        let inspectionCode = errno
        guard inspectionCode == ENOENT else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The settings path could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: inspectionCode) ?? .EIO))
        }
        guard (1024...65435).contains(preferredPort) else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The initial port must be from 1024 to 65435.", cause: nil)
        }
        let port = try availablePort(startingAt: preferredPort, file: file)
        let document = """
        <?xml version="1.0" encoding="utf-8"?>
        <NetworkConfiguration>
          <InternalHttpPort>\(port)</InternalHttpPort>
          <PublicHttpPort>\(port)</PublicHttpPort>
        </NetworkConfiguration>
        """
        do {
            try Data(document.utf8).write(to: file, options: .withoutOverwriting)
        } catch {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The initial network settings could not be saved.", cause: error)
        }
    }

    private static func availablePort(startingAt first: Int, file: URL) throws -> Int {
        for port in first...(first + 100) {
            let descriptor = socket(AF_INET, SOCK_STREAM, 0)
            guard descriptor >= 0 else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "An initial server port could not be checked.", cause: POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO))
            }
            var address = sockaddr_in()
            address.sin_len = UInt8(MemoryLayout<sockaddr_in>.size)
            address.sin_family = sa_family_t(AF_INET)
            address.sin_port = in_port_t(port).bigEndian
            address.sin_addr = in_addr(s_addr: INADDR_ANY)
            let result = withUnsafePointer(to: &address) { pointer in
                pointer.withMemoryRebound(to: sockaddr.self, capacity: 1) {
                    bind(descriptor, $0, socklen_t(MemoryLayout<sockaddr_in>.size))
                }
            }
            let bindCode = errno
            guard close(descriptor) == 0 else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The port-check socket could not be closed.", cause: POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO))
            }
            if result == 0 { return port }
            guard bindCode == EADDRINUSE else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The initial server port \(port) could not be bound.", cause: POSIXError(POSIXErrorCode(rawValue: bindCode) ?? .EIO))
            }
        }
        throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "All ports from \(first) to \(first + 100) are occupied. Close an unused server and retry.", cause: nil)
    }

    static func load(from file: URL) throws -> NetworkSettings {
        var info = stat()
        if lstat(file.path, &info) != 0 {
            let code = errno
            guard code == ENOENT else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The settings file could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: code) ?? .EIO))
            }
            return try localDefault(file: file)
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The settings path is not a regular file.", cause: nil)
        }
        do {
            guard let root = try XMLDocument(contentsOf: file, options: [.nodePreserveAll]).rootElement() else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The XML document has no root element.", cause: nil)
            }
            let httpsEnabled = try boolean("EnableHttps", root: root, file: file, fallback: false)
            let httpsRequired = try boolean("RequireHttps", root: root, file: file, fallback: false)
            guard !httpsRequired || httpsEnabled else {
                throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "RequireHttps is enabled while EnableHttps is disabled.", cause: nil)
            }
            let portName = httpsRequired ? "InternalHttpsPort" : "InternalHttpPort"
            let portDefault = httpsRequired ? 8920 : 8096
            let port = try integer(portName, root: root, file: file, fallback: portDefault)
            let basePath = text("BaseUrl", root: root) ?? ""
            return try makeSettings(scheme: httpsRequired ? "https" : "http", port: port, basePath: basePath, file: file)
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The XML settings could not be read.", cause: error)
        }
    }

    var publicInfoURL: URL { baseURL.appendingPathComponent("System/Info/Public") }
    var libraryURL: URL { baseURL }

    private static func localDefault(file: URL) throws -> NetworkSettings {
        guard let url = URL(string: "http://127.0.0.1:8096") else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "The default local URL is invalid.", cause: nil)
        }
        return NetworkSettings(baseURL: url)
    }

    private static func text(_ name: String, root: XMLElement) -> String? {
        root.elements(forName: name).first?.stringValue?.trimmingCharacters(in: .whitespacesAndNewlines)
    }

    private static func boolean(_ name: String, root: XMLElement, file: URL, fallback: Bool) throws -> Bool {
        guard let raw = text(name, root: root), !raw.isEmpty else { return fallback }
        guard let value = Bool(raw) else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "\(name) must be true or false.", cause: nil)
        }
        return value
    }

    private static func integer(_ name: String, root: XMLElement, file: URL, fallback: Int) throws -> Int {
        guard let raw = text(name, root: root), !raw.isEmpty else { return fallback }
        guard let value = Int(raw), (1...65535).contains(value) else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "\(name) must be a port from 1 to 65535.", cause: nil)
        }
        return value
    }

    private static func makeSettings(scheme: String, port: Int, basePath: String, file: URL) throws -> NetworkSettings {
        guard !basePath.contains(".."), !basePath.contains("?"), !basePath.contains("#") else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "BaseUrl contains an unsupported path component.", cause: nil)
        }
        var components = URLComponents()
        components.scheme = scheme
        components.host = "127.0.0.1"
        components.port = port
        components.path = basePath.isEmpty ? "" : "/" + basePath.trimmingCharacters(in: CharacterSet(charactersIn: "/"))
        guard let url = components.url else {
            throw LauncherFailure.invalidNetworkConfiguration(path: file, detail: "BaseUrl and port could not form a local server URL.", cause: nil)
        }
        return NetworkSettings(baseURL: url)
    }
}
