// Verifies only the owned server process and refuses redirects or neighboring servers.
import Foundation

private struct PublicServerIdentity: Decodable {
    let Id: String
}

private final class RedirectRejector: NSObject, URLSessionTaskDelegate {
    func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }
}

struct ServerReadinessClient {
    private let redirectRejector = RedirectRejector()
    // Fresh database migrations can exceed short fixed startup timeouts.
    // Bound the wait using the host-load scale; Stop still cancels it.
    private let startupLimitSeconds = LauncherClock.loadScaled(seconds: 600)

    func waitUntilReady(settings: NetworkSettings, paths: LauncherPaths, child: ServerChild) async throws {
        let deadline = MonotonicDeadline(secondsFromNow: startupLimitSeconds)
        var attempts = 0
        var lastFailure: Error?
        while !deadline.isExpired {
            try Task.checkCancellation()
            guard child.process.isRunning else {
                throw LauncherFailure.processExited(path: child.executable, status: child.process.terminationStatus)
            }
            attempts += 1
            do {
                let remaining = deadline.remainingNanoseconds
                try await verifyReady(settings: settings, paths: paths, timeoutSeconds: min(4, max(0.1, Double(remaining) / 1_000_000_000)))
                return
            } catch is CancellationError {
                throw CancellationError()
            } catch let failure as LauncherFailure {
                if case .serverIdentityMismatch = failure { throw failure }
                lastFailure = failure
            } catch {
                lastFailure = error
            }
            let remaining = deadline.remainingNanoseconds
            guard remaining > 0 else { break }
            try await LauncherClock.sleep(nanoseconds: min(1_000_000_000, remaining))
        }
        try Task.checkCancellation()
        throw LauncherFailure.readinessTimedOut(url: settings.publicInfoURL, attempts: attempts, lastFailure: lastFailure)
    }

    func verifyReady(settings: NetworkSettings, paths: LauncherPaths, timeoutSeconds: TimeInterval = 4) async throws {
        let expected = try readDeviceID(at: paths.deviceIDFile, url: settings.publicInfoURL)
        var request = URLRequest(url: settings.publicInfoURL)
        request.httpMethod = "GET"
        request.timeoutInterval = timeoutSeconds
        let session = URLSession(configuration: .ephemeral, delegate: redirectRejector, delegateQueue: nil)
        defer { session.invalidateAndCancel() }
        do {
            let (data, response) = try await session.data(for: request)
            try Task.checkCancellation()
            guard let http = response as? HTTPURLResponse else {
                throw LauncherFailure.readinessRequest(url: settings.publicInfoURL, path: paths.deviceIDFile, status: nil, detail: "The response was not HTTP.", cause: nil)
            }
            guard (200...299).contains(http.statusCode) else {
                throw LauncherFailure.readinessRequest(url: settings.publicInfoURL, path: paths.deviceIDFile, status: http.statusCode, detail: "The endpoint did not return success.", cause: nil)
            }
            let received: String
            do {
                received = try JSONDecoder().decode(PublicServerIdentity.self, from: data).Id
            } catch {
                throw LauncherFailure.readinessRequest(url: settings.publicInfoURL, path: paths.deviceIDFile, status: http.statusCode, detail: "The public identity response did not contain a readable Id.", cause: error)
            }
            guard isGuidN(received), received == expected else {
                throw LauncherFailure.serverIdentityMismatch(expected: expected, received: received, path: settings.publicInfoURL)
            }
        } catch is CancellationError {
            throw CancellationError()
        } catch let failure as LauncherFailure {
            throw failure
        } catch {
            throw LauncherFailure.readinessRequest(url: settings.publicInfoURL, path: paths.deviceIDFile, status: nil, detail: "The HTTP request or TLS validation failed.", cause: error)
        }
    }

    private func readDeviceID(at file: URL, url: URL) throws -> String {
        let data: Data
        do {
            data = try Data(contentsOf: file)
        } catch {
            throw LauncherFailure.readinessRequest(url: url, path: file, status: nil, detail: "The server identity file could not be read.", cause: error)
        }
        guard var value = String(data: data, encoding: .utf8) else {
            throw LauncherFailure.readinessRequest(url: url, path: file, status: nil, detail: "The server identity file is not valid UTF-8.", cause: nil)
        }
        if value.first == "\u{FEFF}" { value.removeFirst() }
        value = value.trimmingCharacters(in: .whitespacesAndNewlines)
        guard isGuidN(value) else {
            throw LauncherFailure.readinessRequest(url: url, path: file, status: nil, detail: "The server identity file does not contain a valid 32-character GUID.", cause: nil)
        }
        return value
    }

    private func isGuidN(_ value: String) -> Bool {
        guard value.count == 32, value.unicodeScalars.allSatisfy({
            CharacterSet(charactersIn: "0123456789abcdefABCDEF").contains($0)
        }) else { return false }
        let groups = [8, 4, 4, 4, 12]
        var cursor = value.startIndex
        let parts = groups.map { count -> String in
            let end = value.index(cursor, offsetBy: count)
            let part = String(value[cursor..<end])
            cursor = end
            return part
        }
        return UUID(uuidString: parts.joined(separator: "-")) != nil
    }
}
