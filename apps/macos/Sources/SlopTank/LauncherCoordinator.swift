// Owns launcher state transitions, exact child attempts, readiness, and graceful shutdown.
import AppKit
import Foundation

enum LauncherState: Equatable {
    case stopped
    case starting
    case running
    case stopping
    case failed(String)
}

@MainActor
final class LauncherCoordinator {
    private let storage: LauncherStorage
    private let processController = ServerProcessController()
    private let readiness = ServerReadinessClient()
    private var paths: LauncherPaths?
    private var instanceLease: SingleInstanceLease?
    private var activeAttempt: UUID?
    private var readinessTask: Task<Void, Never>?
    private var openLibraryTask: Task<Void, Never>?
    private var stopTimeoutTask: Task<Void, Never>?
    private var failureAfterStop: Error?
    private var terminationRequested = false
    private let gracefulStopSeconds = 60

    private(set) var state: LauncherState = .stopped {
        didSet { onStateChange?(state) }
    }
    var onStateChange: ((LauncherState) -> Void)?
    var onIssue: ((Error, Bool) -> Void)?
    var onIssueCleared: (() -> Void)?
    var onDataLocationChange: (() -> Void)?
    var onStartRequested: (() -> Void)?
    var onStoppedForQuit: (() -> Void)?

    init(storage: LauncherStorage) { self.storage = storage }

    var hasOwnedChild: Bool { processController.child != nil }
    var hasRunningChild: Bool { processController.child?.isRunning == true }
    var canOpenLibrary: Bool { state == .running && hasRunningChild }
    var canStop: Bool { hasOwnedChild }
    var canChangeDataRoot: Bool { activeAttempt == nil && !hasOwnedChild && (state == .stopped || isFailed) && storage.canChooseDataRoot }
    var dataRootDisplayPath: String { storage.dataRootDisplayPath }

    func start() {
        guard activeAttempt == nil, !hasOwnedChild, state == .stopped || isFailed else { return }
        readinessTask?.cancel()
        openLibraryTask?.cancel()
        failureAfterStop = nil
        terminationRequested = false
        onStartRequested?()
        state = .starting
        do {
            try acquireInstanceLease()
            let paths = try storage.preparePaths()
            self.paths = paths
            try NetworkSettings.prepareFirstRun(at: paths.networkConfiguration)
            let settings = try NetworkSettings.load(from: paths.networkConfiguration)
            let attemptID = UUID()
            activeAttempt = attemptID
            try processController.launch(paths: paths, attemptID: attemptID) { [weak coordinator = self] id, status in
                Task { @MainActor [weak coordinator] in coordinator?.childTerminated(attemptID: id, status: status) }
            }
            guard let child = processController.child, child.attemptID == attemptID else {
                throw LauncherFailure.childAlreadyTracked(pid: processController.child?.process.processIdentifier ?? -1)
            }
            readinessTask = Task { @MainActor [weak self] in
                guard let coordinator = self else { return }
                do {
                    try await coordinator.readiness.waitUntilReady(settings: settings, paths: paths, child: child)
                    guard !Task.isCancelled, coordinator.activeAttempt == attemptID, coordinator.processController.isRunning(attemptID: attemptID) else { return }
                    coordinator.state = .running
                    try coordinator.storage.markStartupCompleted()
                    coordinator.onDataLocationChange?()
                    guard NSWorkspace.shared.open(settings.libraryURL) else {
                        coordinator.onIssue?(LauncherFailure.browserOpen(url: settings.libraryURL), true)
                        return
                    }
                    coordinator.onIssueCleared?()
                } catch is CancellationError {
                    return
                } catch {
                    guard coordinator.activeAttempt == attemptID, !Task.isCancelled else { return }
                    coordinator.failAndStop(error, attemptID: attemptID)
                }
            }
        } catch {
            activeAttempt = nil
            state = .failed(failureDescription(error))
            onIssue?(error, false)
        }
    }

    func stop() {
        if activeAttempt == nil, let child = processController.child { activeAttempt = child.attemptID }
        guard let attemptID = activeAttempt else {
            if terminationRequested { onStoppedForQuit?() }
            return
        }
        readinessTask?.cancel()
        readinessTask = nil
        openLibraryTask?.cancel()
        openLibraryTask = nil
        guard let child = processController.child, child.attemptID == attemptID else {
            let failure = LauncherFailure.childOwnershipMismatch(expected: attemptID, actual: processController.child?.attemptID)
            state = .failed(failureDescription(failure))
            onIssue?(failure, false)
            return
        }
        if !child.isRunning {
            childTerminated(attemptID: attemptID, status: child.process.terminationStatus)
            return
        }
        state = .stopping
        processController.stop(attemptID: attemptID)
        armStopTimeout(child: child)
    }

    func openLibrary() {
        guard canOpenLibrary, let attemptID = activeAttempt, let paths, let child = processController.child else {
            onIssue?(LauncherFailure.processExited(path: paths?.profile ?? URL(fileURLWithPath: dataRootDisplayPath), status: -1), false)
            return
        }
        openLibraryTask?.cancel()
        openLibraryTask = Task { @MainActor [weak self] in
            guard let coordinator = self else { return }
            do {
                let settings = try NetworkSettings.load(from: paths.networkConfiguration)
                try await coordinator.readiness.verifyReady(settings: settings, paths: paths)
                guard !Task.isCancelled, coordinator.activeAttempt == attemptID, coordinator.processController.isRunning(attemptID: attemptID), child.attemptID == attemptID else { return }
                guard NSWorkspace.shared.open(settings.libraryURL) else {
                    coordinator.onIssue?(LauncherFailure.browserOpen(url: settings.libraryURL), true)
                    return
                }
                coordinator.onIssueCleared?()
            } catch is CancellationError {
                return
            } catch {
                guard !Task.isCancelled, coordinator.activeAttempt == attemptID else { return }
                coordinator.onIssue?(error, true)
            }
        }
    }

    func chooseDataRoot() {
        guard canChangeDataRoot else { return }
        do {
            try acquireInstanceLease()
            try storage.chooseDataRoot()
            onDataLocationChange?()
        } catch {
            onIssue?(error, false)
        }
    }

    func retry() {
        if hasOwnedChild { stop() } else { start() }
    }

    func revealLog() {
        let log = paths?.serverLog ?? URL(fileURLWithPath: dataRootDisplayPath).appendingPathComponent("Logs/server.log")
        NSWorkspace.shared.activateFileViewerSelecting([log])
    }

    func requestQuit() {
        terminationRequested = true
        if activeAttempt == nil, let child = processController.child { activeAttempt = child.attemptID }
        if hasOwnedChild { stop() } else { onStoppedForQuit?() }
    }

    private var isFailed: Bool {
        if case .failed = state { return true }
        return false
    }

    private func acquireInstanceLease() throws {
        guard instanceLease == nil else { return }
        let lease = SingleInstanceLease()
        let support = try storage.prepareSupportDirectoryForLock()
        try lease.acquire(at: support.appendingPathComponent("launcher.lock"))
        instanceLease = lease
    }

    private func failAndStop(_ error: Error, attemptID: UUID) {
        guard activeAttempt == attemptID else { return }
        failureAfterStop = error
        stop()
    }

    private func childTerminated(attemptID: UUID, status: Int32) {
        guard activeAttempt == attemptID else { return }
        let executable = processController.child?.executable ?? Bundle.main.bundleURL.appendingPathComponent("Contents/Resources/Runtimes/\((try? currentMacOSRuntimeID()) ?? "unsupported")/Server/jellyfin")
        readinessTask?.cancel()
        readinessTask = nil
        openLibraryTask?.cancel()
        openLibraryTask = nil
        stopTimeoutTask?.cancel()
        stopTimeoutTask = nil
        do {
            _ = try processController.finish(attemptID: attemptID)
        } catch {
            guard !processController.isRunning(attemptID: attemptID) else {
                state = .failed(failureDescription(error))
                onIssue?(error, false)
                return
            }
            activeAttempt = nil
            failureAfterStop = nil
            state = .failed(failureDescription(error))
            onIssue?(error, false)
            return
        }
        activeAttempt = nil
        if terminationRequested {
            state = .stopped
            onStoppedForQuit?()
        } else if let failureAfterStop {
            self.failureAfterStop = nil
            state = .failed(failureDescription(failureAfterStop))
            onIssue?(failureAfterStop, false)
        } else if state == .stopping {
            state = .stopped
        } else {
            let failure = LauncherFailure.processExited(path: executable, status: status)
            state = .failed(failureDescription(failure))
            onIssue?(failure, false)
        }
    }

    private func armStopTimeout(child: ServerChild) {
        stopTimeoutTask?.cancel()
        stopTimeoutTask = Task { @MainActor [weak self] in
            do {
                try await LauncherClock.sleep(nanoseconds: UInt64(gracefulStopSeconds) * 1_000_000_000)
            } catch is CancellationError {
                return
            } catch {
                guard let coordinator = self, coordinator.activeAttempt == child.attemptID else { return }
                let failure = LauncherFailure.stopTimerFailed(path: child.executable, cause: error)
                coordinator.failureAfterStop = failure
                coordinator.state = .failed(coordinator.failureDescription(failure))
                coordinator.onIssue?(failure, false)
                return
            }
            guard let coordinator = self,
                  coordinator.activeAttempt == child.attemptID,
                  coordinator.processController.isRunning(attemptID: child.attemptID) else { return }
            let failure = LauncherFailure.gracefulStopTimedOut(pid: child.process.processIdentifier, seconds: coordinator.gracefulStopSeconds, priorFailure: coordinator.failureAfterStop)
            coordinator.failureAfterStop = failure
            coordinator.state = .failed(coordinator.failureDescription(failure))
            coordinator.onIssue?(failure, false)
        }
    }

    private func failureDescription(_ error: Error) -> String {
        guard let failure = error as? LauncherFailure else { return error.localizedDescription }
        return "[\(failure.code)] \(failure.localizedDescription)\n\nNext step: \(failure.recoverySuggestion ?? "Retry the operation.")"
    }
}
