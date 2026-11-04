// Builds the menu-bar controls from the launcher's current state and data location.
import AppKit

@MainActor
final class LauncherMenuController: NSObject, NSMenuDelegate {
    private let coordinator: LauncherCoordinator
    private let statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private var currentState: LauncherState = .stopped

    init(coordinator: LauncherCoordinator) {
        self.coordinator = coordinator
        super.init()
        statusItem.button?.title = "SlopTank"
        rebuildMenu()
    }

    func updateState(_ state: LauncherState) {
        currentState = state
        rebuildMenu()
    }

    private func rebuildMenu() {
        let menu = NSMenu()
        menu.delegate = self
        let stateLabel = NSMenuItem(title: stateDescription, action: nil, keyEquivalent: "")
        stateLabel.isEnabled = false
        menu.addItem(stateLabel)
        let profileLabel = NSMenuItem(title: "Server data: \(coordinator.dataRootDisplayPath)", action: nil, keyEquivalent: "")
        profileLabel.isEnabled = false
        menu.addItem(profileLabel)
        menu.addItem(.separator())
        let openItem = item("Open Library", #selector(openLibrary))
        openItem.isEnabled = coordinator.canOpenLibrary
        menu.addItem(openItem)
        if currentState == .stopping {
            let stopping = NSMenuItem(title: "Stopping Server...", action: nil, keyEquivalent: "")
            stopping.isEnabled = false
            menu.addItem(stopping)
        } else if coordinator.canStop {
            let stopTitle = isFailed && coordinator.hasOwnedChild ? "Retry Stop" : "Stop Server"
            menu.addItem(item(stopTitle, #selector(stopServer)))
        } else {
            menu.addItem(item("Start or Retry", #selector(startServer)))
        }
        let chooseLocation = item("Choose Server Data Location...", #selector(chooseDataRoot))
        chooseLocation.isEnabled = coordinator.canChangeDataRoot
        menu.addItem(chooseLocation)
        menu.addItem(item("Open Server Log", #selector(openLog)))
        menu.addItem(.separator())
        menu.addItem(item("Quit SlopTank", #selector(quit)))
        statusItem.menu = menu
    }

    private var stateDescription: String {
        switch currentState {
        case .stopped: return "Server stopped"
        case .starting: return "Starting server..."
        case .running: return "Server ready"
        case .stopping: return "Stopping server..."
        case .failed: return "Server needs attention"
        }
    }

    private var isFailed: Bool {
        if case .failed = currentState { return true }
        return false
    }

    private func item(_ title: String, _ action: Selector) -> NSMenuItem {
        let value = NSMenuItem(title: title, action: action, keyEquivalent: "")
        value.target = self
        return value
    }

    @objc private func openLibrary() { coordinator.openLibrary() }
    @objc private func startServer() { coordinator.start() }
    @objc private func stopServer() { coordinator.stop() }
    @objc private func chooseDataRoot() { coordinator.chooseDataRoot() }
    @objc private func openLog() { coordinator.revealLog() }
    @objc private func quit() { coordinator.requestQuit() }
}
