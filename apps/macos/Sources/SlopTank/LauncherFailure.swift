// Typed launch and storage failures retain their original cause and useful location.
import Foundation

enum LauncherFailure: LocalizedError, CustomNSError {
    case missingResource(name: String, path: URL, detail: String, cause: Error?)
    case storage(code: String, action: String, path: URL, cause: Error)
    case dataLocation(code: String, path: URL, detail: String, cause: Error?)
    case profileConfiguration(code: String, path: URL, detail: String, cause: Error?)
    case alreadyRunning(lockPath: URL)
    case processLaunch(path: URL, cause: Error)
    case operationCleanup(code: String, path: URL, primary: Error, cleanup: Error)
    case childAlreadyTracked(pid: Int32)
    case childOwnershipMismatch(expected: UUID, actual: UUID?)
    case processExited(path: URL, status: Int32)
    case readinessTimedOut(url: URL, attempts: Int, lastFailure: Error?)
    case readinessRequest(url: URL, path: URL?, status: Int?, detail: String, cause: Error?)
    case serverIdentityMismatch(expected: String, received: String, path: URL)
    case invalidNetworkConfiguration(path: URL, detail: String, cause: Error?)
    case gracefulStopTimedOut(pid: Int32, seconds: Int, priorFailure: Error?)
    case stopTimerFailed(path: URL, cause: Error)
    case logClose(path: URL, cause: Error)
    case browserOpen(url: URL)

    var code: String {
        switch self {
        case .missingResource: return "resource_missing"
        case .storage(let code, _, _, _): return code
        case .dataLocation(let code, _, _, _): return code
        case .profileConfiguration(let code, _, _, _): return code
        case .alreadyRunning: return "launcher_already_running"
        case .processLaunch: return "server_process_launch_failed"
        case .operationCleanup(let code, _, _, _): return code
        case .childAlreadyTracked: return "server_child_already_tracked"
        case .childOwnershipMismatch: return "server_child_ownership_mismatch"
        case .processExited: return "server_process_exited"
        case .readinessTimedOut: return "server_readiness_timeout"
        case .readinessRequest: return "server_readiness_request_failed"
        case .serverIdentityMismatch: return "server_identity_mismatch"
        case .invalidNetworkConfiguration: return "network_configuration_invalid"
        case .gracefulStopTimedOut: return "server_stop_timeout"
        case .stopTimerFailed: return "server_stop_timer_failed"
        case .logClose: return "server_log_close_failed"
        case .browserOpen: return "browser_open_failed"
        }
    }

    var underlyingCause: Error? {
        switch self {
        case .storage(_, _, _, let cause), .processLaunch(_, let cause), .logClose(_, let cause), .stopTimerFailed(_, let cause): return cause
        case .missingResource(_, _, _, let cause): return cause
        case .operationCleanup(_, _, let primary, _): return primary
        case .dataLocation(_, _, _, let cause), .profileConfiguration(_, _, _, let cause), .readinessTimedOut(_, _, let cause): return cause
        case .gracefulStopTimedOut(_, _, let cause): return cause
        case .readinessRequest(_, _, _, _, let cause), .invalidNetworkConfiguration(_, _, let cause): return cause
        default: return nil
        }
    }

    var errorDescription: String? {
        switch self {
        case let .missingResource(name, path, detail, cause): return "The bundled \(name) at \(path.path) is unavailable: \(detail)\(cause.map { " Cause: \($0.localizedDescription)" } ?? "")"
        case let .storage(_, action, path, cause): return "Could not \(action) at \(path.path): \(cause.localizedDescription)"
        case let .dataLocation(_, path, detail, cause):
            return "The selected server data location at \(path.path) is unavailable: \(detail)\(cause.map { " (\($0.localizedDescription))" } ?? "")"
        case let .profileConfiguration(_, path, detail, cause):
            return "The private SlopTank profile configuration at \(path.path) is unavailable: \(detail)\(cause.map { " Cause: \($0.localizedDescription)" } ?? "")"
        case let .alreadyRunning(path): return "Another SlopTank launcher holds the instance lock at \(path.path)."
        case let .processLaunch(path, cause): return "Could not start the bundled server at \(path.path): \(cause.localizedDescription)"
        case let .operationCleanup(_, path, primary, cleanup): return "Operation failed at \(path.path): \(primary.localizedDescription). Cleanup also failed: \(cleanup.localizedDescription)."
        case let .childAlreadyTracked(pid): return "SlopTank still tracks server process \(pid); it will not start a second child."
        case let .childOwnershipMismatch(expected, actual): return "The server child attempt changed unexpectedly (expected \(expected.uuidString), found \(actual?.uuidString ?? "no tracked child")). SlopTank will not signal another process."
        case let .processExited(path, status): return "The bundled server at \(path.path) exited with status \(status)."
        case let .readinessTimedOut(url, attempts, failure):
            return "The server did not become ready at \(url.absoluteString) after \(attempts) checks. Last error: \(failure?.localizedDescription ?? "none")."
        case let .readinessRequest(url, path, status, detail, cause):
            let response = status.map { "HTTP \($0)" } ?? "no HTTP response"
            let file = path.map { " Identity file: \($0.path)." } ?? ""
            return "Could not verify \(url.absoluteString) (\(response)): \(detail).\(file)\(cause.map { " Cause: \($0.localizedDescription)." } ?? "")"
        case let .serverIdentityMismatch(expected, received, path):
            return "A server at \(path.path) answered with identity \(received); this installation expects \(expected). It was not opened."
        case let .invalidNetworkConfiguration(path, detail, cause):
            return "Invalid network configuration at \(path.path): \(detail)\(cause.map { " Cause: \($0.localizedDescription)" } ?? "")"
        case let .gracefulStopTimedOut(pid, seconds, prior): return "Server process \(pid) did not stop within \(seconds) seconds. It remains tracked by SlopTank.\(prior.map { " The preceding failure was: \($0.localizedDescription)" } ?? "")"
        case let .stopTimerFailed(path, cause): return "Could not track the graceful stop deadline for \(path.path): \(cause.localizedDescription). The child remains tracked."
        case let .logClose(path, cause): return "Could not close the server log at \(path.path): \(cause.localizedDescription)"
        case let .browserOpen(url): return "macOS declined to open the library at \(url.absoluteString) in the default browser."
        }
    }

    var recoverySuggestion: String? {
        switch self {
        case .missingResource: return "Install a complete SlopTank application bundle."
        case .alreadyRunning: return "Use the existing SlopTank menu bar icon."
        case .serverIdentityMismatch: return "SlopTank did not open the other server. Stop the conflicting server or review this profile's network.xml."
        case .invalidNetworkConfiguration: return "Review network.xml. SlopTank does not rewrite server network settings."
        case .gracefulStopTimedOut: return "Use Retry Stop. SlopTank will keep tracking this child and will not launch another one."
        case .operationCleanup, .childAlreadyTracked, .childOwnershipMismatch, .logClose, .stopTimerFailed: return "Open the server log and retry the operation. SlopTank will not signal or start a different child."
        case .browserOpen: return "Choose Open Library again after correcting the macOS default browser. The server remains running."
        case .dataLocation: return "Choose an empty folder or a folder containing a recognized SlopTank profile. No data was moved."
        case .profileConfiguration: return "Restore the private profile configuration or use the recorded data folder to reauthorize access. SlopTank will not create another profile automatically."
        default: return "Open the server log, correct the reported cause, then retry the operation."
        }
    }

    var failureReason: String? { code }
    var canRetryLaunch: Bool {
        if case .alreadyRunning = self { return false }
        return true
    }

    var errorUserInfo: [String: Any] {
        var info: [String: Any] = [
            NSLocalizedDescriptionKey: errorDescription ?? code,
            NSLocalizedFailureReasonErrorKey: code,
            "SlopTankFailureCode": code
        ]
        if let underlyingCause { info[NSUnderlyingErrorKey] = underlyingCause }
        if case let .operationCleanup(_, _, _, cleanup) = self { info["SlopTankCleanupError"] = cleanup }
        return info
    }

    static var errorDomain: String { "org.sloptank.desktop.launcher" }

    var errorCode: Int {
        switch self {
        case .missingResource: return 1001
        case .storage: return 1002
        case .dataLocation: return 1003
        case .profileConfiguration: return 1018
        case .alreadyRunning: return 1004
        case .processLaunch: return 1005
        case .operationCleanup: return 1006
        case .childAlreadyTracked: return 1007
        case .childOwnershipMismatch: return 1008
        case .processExited: return 1009
        case .readinessTimedOut: return 1010
        case .readinessRequest: return 1011
        case .serverIdentityMismatch: return 1012
        case .invalidNetworkConfiguration: return 1013
        case .gracefulStopTimedOut: return 1014
        case .stopTimerFailed: return 1015
        case .logClose: return 1016
        case .browserOpen: return 1017
        }
    }
}
