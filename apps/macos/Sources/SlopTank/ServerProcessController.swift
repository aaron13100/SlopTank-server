// Starts and retains one exact bundled child until its exit and log cleanup complete.
import Darwin
import Foundation

struct ServerChild {
    let attemptID: UUID
    let process: Process
    let logHandle: FileHandle
    let logPath: URL
    let executable: URL

    var isRunning: Bool { process.isRunning }
}

@MainActor
final class ServerProcessController {
    private(set) var child: ServerChild?

    func launch(paths: LauncherPaths, attemptID: UUID, onTermination: @escaping (UUID, Int32) -> Void) throws {
        guard child == nil else { throw LauncherFailure.childAlreadyTracked(pid: child?.process.processIdentifier ?? -1) }
        let resources = Bundle.main.resourceURL ?? Bundle.main.bundleURL.appendingPathComponent("Contents/Resources")
        let runtime = resources.appendingPathComponent("Runtimes", isDirectory: true)
            .appendingPathComponent(try currentMacOSRuntimeID(), isDirectory: true)
        let server = runtime.appendingPathComponent("Server/jellyfin")
        let web = resources.appendingPathComponent("Web", isDirectory: true)
        let ffmpeg = resources.appendingPathComponent("ffmpeg/ffmpeg")
        let ffprobe = resources.appendingPathComponent("ffmpeg/ffprobe")
        let preparationWorker = runtime.appendingPathComponent("Preparation/sloptank-prepare")
        let introSkipper = resources.appendingPathComponent("IntroSkipper", isDirectory: true)
        try requireDirectory(server.deletingLastPathComponent(), name: "server resource folder")
        try requireDirectory(web, name: "web client folder")
        try requireDirectory(ffmpeg.deletingLastPathComponent(), name: "FFmpeg resource folder")
        try requireExecutable(server, name: "server")
        try requireRegularFile(web.appendingPathComponent("index.html"), name: "web client index")
        try requireExecutable(ffmpeg, name: "ffmpeg")
        try requireExecutable(ffprobe, name: "ffprobe")
        try requireExecutable(preparationWorker, name: "media preparation worker")
        try requireRegularFile(introSkipper.appendingPathComponent("meta.json"), name: "intro analysis plugin manifest")
        try requireRegularFile(introSkipper.appendingPathComponent("IntroSkipper.dll"), name: "intro analysis plugin")
        let handle = try openLog(paths.serverLog)
        let process = Process()
        process.executableURL = server
        process.arguments = [
            "--datadir", paths.data.path,
            "--configdir", paths.config.path,
            "--cachedir", paths.cache.path,
            "--logdir", paths.logs.path,
            "--webdir", web.path,
            "--ffmpeg", ffmpeg.path
        ]
        var environment = ProcessInfo.processInfo.environment.filter {
            !$0.key.hasPrefix("JELLYFIN_") && !$0.key.hasPrefix("SLOPTANK_")
        }
        environment["PATH"] = ffmpeg.deletingLastPathComponent().path + ":" + (environment["PATH"] ?? "/usr/bin:/bin:/usr/sbin:/sbin")
        environment["TMPDIR"] = paths.cache.path
        environment["JELLYFIN_Permalinks__AuthorityRoot"] = paths.authority.path
        environment["JELLYFIN_PlaybackPreparation__WorkerPath"] = preparationWorker.path
        environment["JELLYFIN_BundledIntroSkipper__Path"] = introSkipper.path
        process.environment = environment
        process.standardOutput = handle
        process.standardError = handle
        process.terminationHandler = { terminatedProcess in
            onTermination(attemptID, terminatedProcess.terminationStatus)
        }
        let ownedChild = ServerChild(attemptID: attemptID, process: process, logHandle: handle, logPath: paths.serverLog, executable: server)
        child = ownedChild
        do {
            try process.run()
        } catch {
            child = nil
            do {
                try handle.close()
            } catch let closeFailure {
                throw LauncherFailure.operationCleanup(code: "server_launch_log_cleanup_failed", path: paths.serverLog, primary: error, cleanup: closeFailure)
            }
            throw LauncherFailure.processLaunch(path: server, cause: error)
        }
    }

    func isRunning(attemptID: UUID) -> Bool {
        child?.attemptID == attemptID && child?.isRunning == true
    }

    func stop(attemptID: UUID) {
        guard let child, child.attemptID == attemptID, child.isRunning else { return }
        child.process.terminate()
    }

    func finish(attemptID: UUID) throws -> Int32 {
        guard let child, child.attemptID == attemptID else {
            throw LauncherFailure.childOwnershipMismatch(expected: attemptID, actual: self.child?.attemptID)
        }
        guard !child.isRunning else {
            throw LauncherFailure.childAlreadyTracked(pid: child.process.processIdentifier)
        }
        do {
            try child.logHandle.close()
            self.child = nil
            return child.process.terminationStatus
        } catch {
            guard !child.isRunning, self.child?.attemptID == attemptID else {
                throw LauncherFailure.logClose(path: child.logPath, cause: error)
            }
            self.child = nil
            throw LauncherFailure.logClose(path: child.logPath, cause: error)
        }
    }

    private func openLog(_ url: URL) throws -> FileHandle {
        do {
            if !FileManager.default.fileExists(atPath: url.path) {
                guard FileManager.default.createFile(atPath: url.path, contents: nil, attributes: [.posixPermissions: 0o600]) else {
                    throw CocoaError(.fileWriteUnknown)
                }
            }
            let handle = try FileHandle(forWritingTo: url)
            do {
                try handle.seekToEnd()
                return handle
            } catch {
                do {
                    try handle.close()
                } catch let closeFailure {
                    throw LauncherFailure.operationCleanup(code: "server_log_seek_cleanup_failed", path: url, primary: error, cleanup: closeFailure)
                }
                throw error
            }
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.storage(code: "server_log_open_failed", action: "open the server log", path: url, cause: error)
        }
    }

    private func requireExecutable(_ url: URL, name: String) throws {
        var info = stat()
        guard lstat(url.path, &info) == 0 else {
            let failureCode = errno
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The path could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG), info.st_mode & mode_t(0o111) != 0 else {
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The resource must be a regular executable file.", cause: nil)
        }
    }

    private func requireRegularFile(_ url: URL, name: String) throws {
        var info = stat()
        guard lstat(url.path, &info) == 0 else {
            let failureCode = errno
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The path could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The resource must be a regular file.", cause: nil)
        }
    }

    private func requireDirectory(_ url: URL, name: String) throws {
        var info = stat()
        guard lstat(url.path, &info) == 0 else {
            let failureCode = errno
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The path could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR) else {
            throw LauncherFailure.missingResource(name: name, path: url, detail: "The resource must be a regular directory, not a symbolic link.", cause: nil)
        }
    }
}
