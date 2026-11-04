// Composes the launcher UI and selects first-run or automatic startup behavior.
// Selects the first-run welcome or the normal automatic server startup.
import AppKit

@MainActor
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var coordinator: LauncherCoordinator?
    private var menuController: LauncherMenuController?
    private var statusWindowController: LauncherStatusWindowController?
    private var welcomeWindowController: LauncherWelcomeWindowController?

    func applicationDidFinishLaunching(_ notification: Notification) {
        let storage = LauncherStorage()
        let coordinator = LauncherCoordinator(storage: storage)
        self.coordinator = coordinator
        let menuController = LauncherMenuController(coordinator: coordinator)
        self.menuController = menuController
        let statusWindowController = LauncherStatusWindowController(coordinator: coordinator)
        self.statusWindowController = statusWindowController
        coordinator.onStateChange = { [weak menuController, weak statusWindowController] state in
            menuController?.updateState(state)
            statusWindowController?.update(state)
        }
        coordinator.onIssue = { [weak statusWindowController] error, canOpenLibrary in
            statusWindowController?.showIssue(error, canOpenLibrary: canOpenLibrary)
        }
        coordinator.onIssueCleared = { [weak statusWindowController] in statusWindowController?.clearIssue() }
        coordinator.onDataLocationChange = { [weak menuController, weak self] in
            menuController?.updateState(coordinator.state)
            self?.welcomeWindowController?.updateLocation(coordinator.dataRootDisplayPath)
        }
        coordinator.onStartRequested = { [weak self] in self?.welcomeWindowController?.window?.orderOut(nil) }
        coordinator.onStoppedForQuit = { NSApp.terminate(nil) }
        if storage.needsFirstRunWelcome {
            let welcome = LauncherWelcomeWindowController(coordinator: coordinator)
            welcomeWindowController = welcome
            welcome.showWindow(nil)
        } else {
            coordinator.start()
        }
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let coordinator, coordinator.hasOwnedChild else { return .terminateNow }
        coordinator.requestQuit()
        return .terminateCancel
    }
}
