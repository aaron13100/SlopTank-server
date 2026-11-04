// Starts AppKit synchronously so its event loop can service queued main-actor work.
import AppKit

@main
@MainActor
struct LauncherApplication {
    static func main() {
        let application = NSApplication.shared
        application.setActivationPolicy(.accessory)
        let delegate = AppDelegate()
        application.delegate = delegate
        withExtendedLifetime(delegate) {
            application.run()
        }
    }
}
