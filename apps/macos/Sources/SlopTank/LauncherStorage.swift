// Owns app-support state, selected data-location access, network settings, and the process lock.
import AppKit
import Darwin
import Foundation

struct LauncherPaths {
    let support: URL
    let profile: URL
    let data: URL
    let config: URL
    let cache: URL
    let logs: URL
    let authority: URL

    var deviceIDFile: URL { data.appendingPathComponent("data/device.txt") }
    var networkConfiguration: URL { config.appendingPathComponent("network.xml") }
    var serverLog: URL { logs.appendingPathComponent("server.log") }
    var instanceLock: URL { support.appendingPathComponent("launcher.lock") }
}

final class LauncherStorage {
    private let fileManager = FileManager.default
    private let defaults = UserDefaults.standard
    private let dataBookmarkKey = "serverDataRootBookmark"
    private var securityScopedRoot: URL?
    private let configurationStore: ProfileConfigurationStore
    private let supportRoot: URL
    private let defaultDataRoot: URL

    var dataRootDisplayPath: String {
        configurationStore.selectedRootPath
    }

    var canChooseDataRoot: Bool { configurationStore.loadFailure == nil && (!configurationStore.startupCompleted || dataRootDisplayPath != defaultDataRoot.path) }
    var needsFirstRunWelcome: Bool { configurationStore.loadFailure != nil || !configurationStore.startupCompleted }

    init() {
        let supportBase = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory(), isDirectory: true).appendingPathComponent("Library/Application Support", isDirectory: true)
        supportRoot = supportBase.appendingPathComponent("SlopTank", isDirectory: true).standardizedFileURL
        defaultDataRoot = supportRoot.appendingPathComponent("ServerProfile", isDirectory: true).standardizedFileURL
        configurationStore = ProfileConfigurationStore(supportRoot: supportRoot, defaultRoot: defaultDataRoot)
    }

    func preparePaths() throws -> LauncherPaths {
        let support = try prepareSupportDirectory()
        let config = try configurationStore.requireOrInitializeDefault(hasBookmark: defaults.data(forKey: dataBookmarkKey) != nil)
        let profile = try resolveDataRoot(configuration: config)
        try configurationStore.ensureProfileMarkers(config)
        let paths = LauncherPaths(
            support: support,
            profile: profile,
            data: profile.appendingPathComponent("Data", isDirectory: true),
            config: profile.appendingPathComponent("Configuration", isDirectory: true),
            cache: profile.appendingPathComponent("Cache", isDirectory: true),
            logs: profile.appendingPathComponent("Logs", isDirectory: true),
            authority: support.appendingPathComponent("PermalinkAuthority", isDirectory: true)
        )
        do {
            for directory in [paths.data, paths.config, paths.cache, paths.logs, paths.authority] {
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
                try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: directory.path)
            }
        } catch {
            throw LauncherFailure.storage(code: "profile_directory_create_failed", action: "create private server folders", path: profile, cause: error)
        }
        return paths
    }

    func chooseDataRoot() throws {
        let hasStoredBookmark = defaults.data(forKey: dataBookmarkKey) != nil
        _ = try configurationStore.requireOrInitializeDefault(hasBookmark: hasStoredBookmark)
        guard canChooseDataRoot else { throw locationLockedFailure() }
        let panel = NSOpenPanel()
        panel.title = "Choose Server Data Location"
        panel.message = configurationStore.startupCompleted
            ? "Reauthorize the recorded server data folder. SlopTank will not move data or switch profiles."
            : "Choose an empty folder. Existing data is never moved."
        panel.prompt = "Use This Folder"
        panel.canChooseFiles = false
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = false
        guard panel.runModal() == .OK, let selected = panel.url else { return }
        if configurationStore.startupCompleted && selected.standardizedFileURL.path != dataRootDisplayPath {
            throw locationLockedFailure()
        }
        let alreadyActive = securityScopedRoot?.standardizedFileURL.path == selected.standardizedFileURL.path
        let scoped = alreadyActive || selected.startAccessingSecurityScopedResource()
        guard scoped else {
            throw LauncherFailure.dataLocation(code: "profile_folder_access_denied", path: selected, detail: "macOS did not grant access to this folder.", cause: nil)
        }
        do {
            let bookmark = try selected.bookmarkData(options: [.withSecurityScope], includingResourceValuesForKeys: nil, relativeTo: nil)
            let config = try configurationStore.chooseRoot(selected, hasBookmark: hasStoredBookmark)
            try configurationStore.ensureProfileMarkers(config)
            defaults.set(bookmark, forKey: dataBookmarkKey)
        } catch {
            if !alreadyActive { selected.stopAccessingSecurityScopedResource() }
            throw error
        }
        if !alreadyActive {
            securityScopedRoot?.stopAccessingSecurityScopedResource()
            securityScopedRoot = selected
        }
    }

    func markStartupCompleted() throws { try configurationStore.markStartupCompleted() }

    func prepareSupportDirectoryForLock() throws -> URL { try prepareSupportDirectory() }

    deinit { securityScopedRoot?.stopAccessingSecurityScopedResource() }

    private func prepareSupportDirectory() throws -> URL {
        do {
            try fileManager.createDirectory(at: supportRoot, withIntermediateDirectories: true)
            try fileManager.setAttributes([.posixPermissions: 0o700], ofItemAtPath: supportRoot.path)
            return supportRoot
        } catch {
            throw LauncherFailure.storage(code: "application_support_create_failed", action: "create private application storage", path: supportRoot, cause: error)
        }
    }

    private func resolveDataRoot(configuration: PrivateProfileConfiguration) throws -> URL {
        let recorded = URL(fileURLWithPath: configuration.selectedRootPath, isDirectory: true).standardizedFileURL
        guard recorded.path != defaultDataRoot.path else { return defaultDataRoot }
        guard let bookmark = defaults.data(forKey: dataBookmarkKey) else {
            throw LauncherFailure.dataLocation(code: "profile_bookmark_missing", path: recorded, detail: "The private profile records this folder, but its security bookmark is missing. Reauthorize the same folder; SlopTank will not create a replacement.", cause: nil)
        }
        do {
            var stale = false
            let selected = try URL(resolvingBookmarkData: bookmark, options: [.withSecurityScope], relativeTo: nil, bookmarkDataIsStale: &stale)
            guard !stale else {
                throw LauncherFailure.dataLocation(code: "profile_bookmark_stale", path: recorded, detail: "The saved folder bookmark is stale. Reauthorize the same recorded folder.", cause: nil)
            }
            guard selected.standardizedFileURL.path == recorded.path else {
                throw LauncherFailure.dataLocation(code: "profile_bookmark_identity_mismatch", path: recorded, detail: "The bookmark resolves to a different folder than the private profile records.", cause: nil)
            }
            if securityScopedRoot?.standardizedFileURL.path != selected.standardizedFileURL.path {
                guard selected.startAccessingSecurityScopedResource() else {
                    throw LauncherFailure.dataLocation(code: "profile_folder_unavailable", path: selected, detail: "macOS could not reopen the selected folder. SlopTank will not create a replacement profile.", cause: nil)
                }
                securityScopedRoot?.stopAccessingSecurityScopedResource()
                securityScopedRoot = selected
            }
            return selected
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.dataLocation(code: "profile_bookmark_invalid", path: recorded, detail: "The saved folder bookmark could not be resolved. Reauthorize this folder; SlopTank will not fall back to a new profile.", cause: error)
        }
    }

    private func locationLockedFailure() -> LauncherFailure {
        LauncherFailure.dataLocation(code: "profile_location_locked", path: URL(fileURLWithPath: dataRootDisplayPath), detail: "The server data location is fixed after first successful startup. You may reauthorize only this recorded folder.", cause: nil)
    }
}

final class SingleInstanceLease {
    private var descriptor: Int32 = -1

    func acquire(at url: URL) throws {
        guard descriptor < 0 else { throw LauncherFailure.storage(code: "launcher_lock_already_open", action: "open the launcher lock", path: url, cause: CocoaError(.fileReadUnknown)) }
        descriptor = open(url.path, O_CREAT | O_RDWR | O_CLOEXEC | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else {
            let failureCode = errno
            throw LauncherFailure.storage(code: "launcher_lock_open_failed", action: "open the launcher lock", path: url, cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        var details = stat()
        guard fstat(descriptor, &details) == 0 else {
            let failureCode = errno
            let primary = POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO)
            try closeAfterFailure(path: url, primary: primary)
            throw LauncherFailure.storage(code: "launcher_lock_stat_failed", action: "inspect the launcher lock", path: url, cause: primary)
        }
        guard details.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
            let primary = CocoaError(.fileReadCorruptFile)
            try closeAfterFailure(path: url, primary: primary)
            throw LauncherFailure.storage(code: "launcher_lock_not_regular", action: "validate the launcher lock", path: url, cause: primary)
        }
        guard details.st_uid == geteuid() else {
            let primary = CocoaError(.fileReadNoPermission)
            try closeAfterFailure(path: url, primary: primary)
            throw LauncherFailure.storage(code: "launcher_lock_wrong_owner", action: "validate launcher lock ownership", path: url, cause: primary)
        }
        guard flock(descriptor, LOCK_EX | LOCK_NB) == 0 else {
            let failureCode = errno
            let primary = POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO)
            try closeAfterFailure(path: url, primary: primary)
            if failureCode == EWOULDBLOCK || failureCode == EAGAIN {
                throw LauncherFailure.alreadyRunning(lockPath: url)
            }
            throw LauncherFailure.storage(code: "launcher_lock_failed", action: "lock the launcher instance", path: url, cause: primary)
        }
    }

    private func closeAfterFailure(path: URL, primary: Error) throws {
        guard close(descriptor) == 0 else {
            let failureCode = errno
            descriptor = -1
            throw LauncherFailure.operationCleanup(
                code: "launcher_lock_cleanup_failed",
                path: path,
                primary: primary,
                cleanup: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO)
            )
        }
        descriptor = -1
    }
}
