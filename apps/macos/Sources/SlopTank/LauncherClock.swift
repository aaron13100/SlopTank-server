// Provides monotonic deadlines and cancellation-aware waits for process operations.
import Darwin
import Foundation

struct MonotonicDeadline {
    private let end: UInt64

    init(secondsFromNow: TimeInterval) {
        let duration = UInt64(max(0, secondsFromNow) * 1_000_000_000)
        end = DispatchTime.now().uptimeNanoseconds &+ duration
    }

    var remainingNanoseconds: UInt64 {
        let now = DispatchTime.now().uptimeNanoseconds
        return now >= end ? 0 : end - now
    }

    var isExpired: Bool { remainingNanoseconds == 0 }
}

enum LauncherClock {
    /// Mirrors the repository's bounded load scale for startup on a contended host.
    static func loadScaled(seconds: TimeInterval) -> TimeInterval {
        var load = 0.0
        guard getloadavg(&load, 1) == 1 else { return seconds }
        let processors = max(1, ProcessInfo.processInfo.activeProcessorCount)
        return seconds * max(1, min(20, load / Double(processors)))
    }

    static func sleep(nanoseconds: UInt64) async throws {
        try await Task.sleep(nanoseconds: nanoseconds)
    }
}
