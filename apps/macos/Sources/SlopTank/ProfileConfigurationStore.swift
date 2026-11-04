// Persists the private profile identity that binds data and permalink authority.
import Darwin
import Foundation

enum ProfileInitializationState: String, Codable {
    case initializing
    case ready
}

struct PrivateProfileConfiguration: Codable, Equatable {
    let schemaVersion: Int
    let profileID: String
    var selectedRootPath: String
    var startupCompleted: Bool
    var initializationState: ProfileInitializationState
}

final class ProfileConfigurationStore {
    private let fileManager = FileManager.default
    let fileURL: URL
    let supportRoot: URL
    let defaultRoot: URL
    let authorityRoot: URL
    private(set) var configuration: PrivateProfileConfiguration?
    private(set) var loadFailure: LauncherFailure?
    private let dataMarkerName = ".sloptank-profile"
    private let authorityMarkerName = ".sloptank-authority-profile"

    init(supportRoot: URL, defaultRoot: URL) {
        self.supportRoot = supportRoot
        self.defaultRoot = defaultRoot.standardizedFileURL
        self.authorityRoot = supportRoot.appendingPathComponent("PermalinkAuthority", isDirectory: true)
        self.fileURL = supportRoot.appendingPathComponent("profile.json")
        do {
            configuration = try readConfiguration()
        } catch let failure as LauncherFailure {
            loadFailure = failure
        } catch {
            loadFailure = LauncherFailure.profileConfiguration(code: "profile_config_read_failed", path: fileURL, detail: "The private configuration could not be read.", cause: error)
        }
    }

    var selectedRootPath: String {
        configuration?.selectedRootPath ?? (loadFailure == nil ? defaultRoot.path : "Unavailable; restore \(fileURL.path)")
    }
    var startupCompleted: Bool { configuration?.startupCompleted ?? false }
    var hasConfiguration: Bool { configuration != nil }

    func requireOrInitializeDefault(hasBookmark: Bool) throws -> PrivateProfileConfiguration {
        try throwLoadFailureIfPresent()
        if let configuration {
            return try resumeInitializationIfNeeded(configuration)
        }
        guard !hasBookmark else {
            throw LauncherFailure.profileConfiguration(code: "profile_config_missing", path: fileURL, detail: "A stored data-folder bookmark exists but its private profile configuration is missing.", cause: nil)
        }
        try ensureEmptyDirectory(defaultRoot, createIfMissing: true)
        try ensureEmptyDirectory(authorityRoot, createIfMissing: true)
        let initial = PrivateProfileConfiguration(schemaVersion: 1, profileID: UUID().uuidString.lowercased(), selectedRootPath: defaultRoot.path, startupCompleted: false, initializationState: .initializing)
        try persist(initial)
        return try completeInitialization(initial)
    }

    func chooseRoot(_ selected: URL, hasBookmark: Bool) throws -> PrivateProfileConfiguration {
        try throwLoadFailureIfPresent()
        let normalized = selected.standardizedFileURL
        if let current = configuration {
            let ready = try resumeInitializationIfNeeded(current)
            if ready.startupCompleted {
                guard normalized.path == ready.selectedRootPath else {
                    throw LauncherFailure.dataLocation(code: "profile_location_locked", path: normalized, detail: "The server data location is fixed after first startup; only the recorded folder can be reauthorized.", cause: nil)
                }
                try ensureProfileMarkers(ready)
                return ready
            }
            try ensureAuthorityMarker(profileID: ready.profileID, allowCreate: false)
            if normalized.path == ready.selectedRootPath {
                try ensureRootMarker(normalized, profileID: ready.profileID, allowCreate: false)
                return ready
            }
            if normalized.path != ready.selectedRootPath {
                try ensureEmptyDirectory(normalized, createIfMissing: false)
            }
            var updated = ready
            updated.selectedRootPath = normalized.path
            updated.initializationState = .initializing
            try persist(updated)
            return try completeInitialization(updated)
        }
        guard !hasBookmark else {
            throw LauncherFailure.profileConfiguration(code: "profile_config_missing", path: fileURL, detail: "The private profile configuration is missing while a data-folder bookmark remains.", cause: nil)
        }
        try ensureEmptyDirectory(authorityRoot, createIfMissing: true)
        try ensureEmptyDirectory(normalized, createIfMissing: false)
        let initial = PrivateProfileConfiguration(schemaVersion: 1, profileID: UUID().uuidString.lowercased(), selectedRootPath: normalized.path, startupCompleted: false, initializationState: .initializing)
        try persist(initial)
        return try completeInitialization(initial)
    }

    func markStartupCompleted() throws {
        try throwLoadFailureIfPresent()
        guard var configuration else {
            throw LauncherFailure.profileConfiguration(code: "profile_config_missing", path: fileURL, detail: "The private profile has not been initialized.", cause: nil)
        }
        guard configuration.initializationState == .ready else {
            throw LauncherFailure.profileConfiguration(code: "profile_initialization_incomplete", path: fileURL, detail: "The private profile markers have not been established.", cause: nil)
        }
        guard !configuration.startupCompleted else { return }
        configuration.startupCompleted = true
        try persist(configuration)
    }

    func ensureProfileMarkers(_ configuration: PrivateProfileConfiguration) throws {
        let root = URL(fileURLWithPath: configuration.selectedRootPath, isDirectory: true)
        try ensureRootMarker(root, profileID: configuration.profileID, allowCreate: false)
        try ensureAuthorityMarker(profileID: configuration.profileID, allowCreate: false)
    }

    private func resumeInitializationIfNeeded(_ configuration: PrivateProfileConfiguration) throws -> PrivateProfileConfiguration {
        guard configuration.initializationState == .initializing else {
            try ensureProfileMarkers(configuration)
            return configuration
        }
        return try completeInitialization(configuration)
    }

    private func completeInitialization(_ pending: PrivateProfileConfiguration) throws -> PrivateProfileConfiguration {
        guard pending.schemaVersion == 1,
              configuration == pending,
              pending.initializationState == .initializing else {
            throw LauncherFailure.profileConfiguration(code: "profile_initialization_identity_mismatch", path: fileURL, detail: "The pending initialization no longer matches the retained private profile identity.", cause: nil)
        }
        let root = URL(fileURLWithPath: pending.selectedRootPath, isDirectory: true)
        try ensurePendingMarker(root.appendingPathComponent(dataMarkerName), parent: root, profileID: pending.profileID, createParent: root.standardizedFileURL.path == defaultRoot.path, allowExistingContents: false)
        try ensurePendingMarker(authorityRoot.appendingPathComponent(authorityMarkerName), parent: authorityRoot, profileID: pending.profileID, createParent: true, allowExistingContents: true)
        var ready = pending
        ready.initializationState = .ready
        try persist(ready)
        return ready
    }

    private func ensurePendingMarker(_ marker: URL, parent: URL, profileID: String, createParent: Bool, allowExistingContents: Bool) throws {
        var info = stat()
        if lstat(marker.path, &info) == 0 {
            try ensureMarker(marker, parent: parent, profileID: profileID, allowCreate: false)
            if !allowExistingContents {
                do {
                    let entries = try fileManager.contentsOfDirectory(atPath: parent.path)
                    guard entries == [marker.lastPathComponent] else {
                        throw LauncherFailure.dataLocation(code: "profile_pending_directory_unexpected_files", path: parent, detail: "The pending profile directory contains files beyond its matching marker.", cause: nil)
                    }
                } catch let failure as LauncherFailure {
                    throw failure
                } catch {
                    throw LauncherFailure.dataLocation(code: "profile_folder_inspection_failed", path: parent, detail: "The pending profile directory could not be inspected.", cause: error)
                }
            }
            return
        }
        let failureCode = errno
        guard failureCode == ENOENT else {
            throw LauncherFailure.dataLocation(code: "profile_marker_inspection_failed", path: marker, detail: "The pending identity marker could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        try ensureEmptyDirectory(parent, createIfMissing: createParent)
        try ensureMarker(marker, parent: parent, profileID: profileID, allowCreate: true)
    }

    func ensureEmptyDirectory(_ directory: URL, createIfMissing: Bool) throws {
        var info = stat()
        if lstat(directory.path, &info) != 0 {
            let failureCode = errno
            guard failureCode == ENOENT, createIfMissing else {
                throw LauncherFailure.dataLocation(code: "profile_folder_inspection_failed", path: directory, detail: "The folder is unavailable.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
            }
            do {
                try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
            } catch {
                throw LauncherFailure.dataLocation(code: "profile_folder_create_failed", path: directory, detail: "The folder could not be created.", cause: error)
            }
            return
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR) else {
            throw LauncherFailure.dataLocation(code: "profile_folder_not_directory", path: directory, detail: "The selected path is not a regular directory.", cause: nil)
        }
        do {
            guard try fileManager.contentsOfDirectory(atPath: directory.path).isEmpty else {
                throw LauncherFailure.dataLocation(code: "profile_folder_not_empty", path: directory, detail: "The folder contains unrecognized files.", cause: nil)
            }
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.dataLocation(code: "profile_folder_inspection_failed", path: directory, detail: "The folder contents could not be inspected.", cause: error)
        }
    }

    private func readConfiguration() throws -> PrivateProfileConfiguration? {
        var info = stat()
        if lstat(fileURL.path, &info) != 0 {
            let failureCode = errno
            guard failureCode == ENOENT else {
                throw LauncherFailure.profileConfiguration(code: "profile_config_inspection_failed", path: fileURL, detail: "The private configuration path could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
            }
            return nil
        }
        guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
            throw LauncherFailure.profileConfiguration(code: "profile_config_not_regular", path: fileURL, detail: "The private configuration must be a regular file.", cause: nil)
        }
        do {
            let data = try Data(contentsOf: fileURL)
            guard let object = try JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let version = object["schemaVersion"] as? Int else {
                throw LauncherFailure.profileConfiguration(code: "profile_config_corrupt", path: fileURL, detail: "The file does not contain a valid versioned profile record.", cause: nil)
            }
            guard version == 1 else {
                throw LauncherFailure.profileConfiguration(code: "profile_config_version_unsupported", path: fileURL, detail: "Schema version \(version) is not supported by this launcher.", cause: nil)
            }
            let value = try JSONDecoder().decode(PrivateProfileConfiguration.self, from: data)
            guard value.schemaVersion == 1,
                  UUID(uuidString: value.profileID) != nil,
                  URL(fileURLWithPath: value.selectedRootPath).standardizedFileURL.path == value.selectedRootPath,
                  value.selectedRootPath.hasPrefix("/") else {
                throw LauncherFailure.profileConfiguration(code: "profile_config_corrupt", path: fileURL, detail: "The profile identity or selected path is invalid.", cause: nil)
            }
            return value
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.profileConfiguration(code: "profile_config_decode_failed", path: fileURL, detail: "The private configuration could not be decoded.", cause: error)
        }
    }

    private func persist(_ value: PrivateProfileConfiguration) throws {
        do {
            let encoder = JSONEncoder()
            encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
            try encoder.encode(value).write(to: fileURL, options: [.atomic])
            try fileManager.setAttributes([.posixPermissions: 0o600], ofItemAtPath: fileURL.path)
            configuration = value
        } catch {
            throw LauncherFailure.profileConfiguration(code: "profile_config_write_failed", path: fileURL, detail: "The versioned private profile record could not be saved atomically.", cause: error)
        }
    }

    private func ensureRootMarker(_ root: URL, profileID: String, allowCreate: Bool) throws {
        try ensureMarker(root.appendingPathComponent(dataMarkerName), parent: root, profileID: profileID, allowCreate: allowCreate)
    }

    private func ensureAuthorityMarker(profileID: String, allowCreate: Bool) throws {
        try ensureMarker(authorityRoot.appendingPathComponent(authorityMarkerName), parent: authorityRoot, profileID: profileID, allowCreate: allowCreate)
    }

    private func ensureMarker(_ marker: URL, parent: URL, profileID: String, allowCreate: Bool) throws {
        var parentInfo = stat()
        guard lstat(parent.path, &parentInfo) == 0 else {
            let failureCode = errno
            throw LauncherFailure.dataLocation(code: "profile_marker_parent_unavailable", path: parent, detail: "The profile identity directory could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        guard parentInfo.st_mode & mode_t(S_IFMT) == mode_t(S_IFDIR) else {
            throw LauncherFailure.dataLocation(code: "profile_marker_parent_invalid", path: parent, detail: "The profile identity directory must be a real directory, not a symlink or other file.", cause: nil)
        }
        var info = stat()
        if lstat(marker.path, &info) == 0 {
            guard info.st_mode & mode_t(S_IFMT) == mode_t(S_IFREG) else {
                throw LauncherFailure.dataLocation(code: "profile_marker_not_regular", path: marker, detail: "The identity marker is not a regular file.", cause: nil)
            }
            do {
                let expected = "SlopTank profile version 1\nprofileId=\(profileID)\n"
                let actual = try String(contentsOf: marker, encoding: .utf8)
                guard actual == expected else {
                    throw LauncherFailure.dataLocation(code: "profile_identity_mismatch", path: marker, detail: "The marker belongs to a different or unsupported profile.", cause: nil)
                }
                return
            } catch let failure as LauncherFailure {
                throw failure
            } catch {
                throw LauncherFailure.dataLocation(code: "profile_marker_read_failed", path: marker, detail: "The identity marker could not be read.", cause: error)
            }
        }
        let failureCode = errno
        guard failureCode == ENOENT else {
            throw LauncherFailure.dataLocation(code: "profile_marker_inspection_failed", path: marker, detail: "The identity marker could not be inspected.", cause: POSIXError(POSIXErrorCode(rawValue: failureCode) ?? .EIO))
        }
        guard allowCreate else {
            throw LauncherFailure.dataLocation(code: "profile_marker_missing", path: marker, detail: "A required profile identity marker is missing.", cause: nil)
        }
        try ensureEmptyDirectory(parent, createIfMissing: parent == authorityRoot)
        let contents = Data("SlopTank profile version 1\nprofileId=\(profileID)\n".utf8)
        guard fileManager.createFile(atPath: marker.path, contents: contents, attributes: [.posixPermissions: 0o600]) else {
            throw LauncherFailure.dataLocation(code: "profile_marker_create_failed", path: marker, detail: "The identity marker could not be created.", cause: CocoaError(.fileWriteUnknown))
        }
    }

    private func throwLoadFailureIfPresent() throws {
        if let loadFailure { throw loadFailure }
    }
}
