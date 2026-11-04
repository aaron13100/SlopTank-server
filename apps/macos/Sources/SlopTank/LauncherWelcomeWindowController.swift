// Offers the first-run start action and optional pre-start server-data location.
import AppKit

@MainActor
final class LauncherWelcomeWindowController: NSWindowController {
    private let coordinator: LauncherCoordinator
    private let locationLabel = NSTextField(wrappingLabelWithString: "")
    private let startButton = NSButton(title: "Start SlopTank", target: nil, action: nil)
    private let chooseButton = NSButton(title: "Use another drive...", target: nil, action: nil)

    init(coordinator: LauncherCoordinator) {
        self.coordinator = coordinator
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 500, height: 250),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "Welcome to SlopTank"
        window.center()
        super.init(window: window)
        configureContents()
        updateLocation(coordinator.dataRootDisplayPath)
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    func updateLocation(_ path: String) {
        locationLabel.stringValue = "Server data will be stored here:\n\(path)\n\nSlopTank will not move or delete existing files. This location is fixed after the first successful startup."
    }

    private func configureContents() {
        guard let content = window?.contentView else { return }
        let title = NSTextField(labelWithString: "Your private media server")
        title.font = .boldSystemFont(ofSize: 20)
        locationLabel.maximumNumberOfLines = 0
        locationLabel.lineBreakMode = .byWordWrapping
        startButton.target = self
        startButton.action = #selector(start)
        startButton.keyEquivalent = "\r"
        chooseButton.target = self
        chooseButton.action = #selector(chooseLocation)
        [title, locationLabel, startButton, chooseButton].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview($0)
        }
        NSLayoutConstraint.activate([
            title.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            title.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            title.topAnchor.constraint(equalTo: content.topAnchor, constant: 22),
            locationLabel.leadingAnchor.constraint(equalTo: title.leadingAnchor),
            locationLabel.trailingAnchor.constraint(equalTo: title.trailingAnchor),
            locationLabel.topAnchor.constraint(equalTo: title.bottomAnchor, constant: 12),
            chooseButton.leadingAnchor.constraint(equalTo: title.leadingAnchor),
            chooseButton.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -18),
            startButton.trailingAnchor.constraint(equalTo: title.trailingAnchor),
            startButton.centerYAnchor.constraint(equalTo: chooseButton.centerYAnchor)
        ])
    }

    @objc private func start() {
        window?.orderOut(nil)
        coordinator.start()
    }

    @objc private func chooseLocation() {
        coordinator.chooseDataRoot()
        updateLocation(coordinator.dataRootDisplayPath)
    }
}
