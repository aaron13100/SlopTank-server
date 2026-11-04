// Presents startup progress, shutdown state, and recoverable typed failures.
import AppKit

@MainActor
final class LauncherStatusWindowController: NSWindowController {
    private let coordinator: LauncherCoordinator
    private let titleLabel = NSTextField(labelWithString: "Starting SlopTank")
    private let detailView = NSTextView()
    private let detailScroll = NSScrollView()
    private let spinner = NSProgressIndicator()
    private let retryButton = NSButton(title: "Retry", target: nil, action: nil)
    private let openLibraryButton = NSButton(title: "Open Library", target: nil, action: nil)
    private let logButton = NSButton(title: "Open Server Log", target: nil, action: nil)

    init(coordinator: LauncherCoordinator) {
        self.coordinator = coordinator
        let window = NSWindow(
            contentRect: NSRect(x: 0, y: 0, width: 580, height: 330),
            styleMask: [.titled, .closable],
            backing: .buffered,
            defer: false
        )
        window.title = "SlopTank"
        window.center()
        super.init(window: window)
        configureContents()
    }

    @available(*, unavailable)
    required init?(coder: NSCoder) { nil }

    func update(_ state: LauncherState) {
        switch state {
        case .starting:
            titleLabel.stringValue = "Starting SlopTank"
            setDetail("The server is starting. This window will close when the library is ready.")
            showSpinner(true)
            showWindow(nil)
        case .stopping:
            titleLabel.stringValue = "Stopping SlopTank"
            setDetail("Waiting for the bundled server to stop safely.")
            showSpinner(true)
            showWindow(nil)
        case .failed(let detail):
            titleLabel.stringValue = "SlopTank needs attention"
            setDetail(detail)
            showSpinner(false)
            retryButton.isHidden = false
            logButton.isHidden = false
            showWindow(nil)
        case .running:
            showSpinner(false)
            window?.orderOut(nil)
        case .stopped:
            titleLabel.stringValue = "SlopTank is stopped"
            setDetail("Use the SlopTank menu bar icon to start the bundled server.")
            showSpinner(false)
            retryButton.isHidden = true
            logButton.isHidden = true
            window?.orderOut(nil)
        }
    }

    func showIssue(_ error: Error, canOpenLibrary: Bool) {
        titleLabel.stringValue = "SlopTank needs attention"
        let typed = error as? LauncherFailure
        let code = typed?.code ?? "unexpected_error"
        let recovery = typed?.recoverySuggestion ?? "Open the server log and retry the operation."
        setDetail("[\(code)] \(error.localizedDescription)\n\nNext step: \(recovery)")
        showSpinner(false)
        retryButton.title = coordinator.hasOwnedChild ? "Retry Stop" : "Retry Start"
        retryButton.isHidden = canOpenLibrary || !(typed?.canRetryLaunch ?? true)
        openLibraryButton.isHidden = !canOpenLibrary
        logButton.isHidden = false
        showWindow(nil)
    }

    func clearIssue() { window?.orderOut(nil) }

    private func configureContents() {
        guard let content = window?.contentView else { return }
        titleLabel.font = .boldSystemFont(ofSize: 18)
        detailView.isEditable = false
        detailView.isSelectable = true
        detailView.drawsBackground = false
        detailView.isVerticallyResizable = true
        detailView.isHorizontallyResizable = false
        detailView.autoresizingMask = [.width]
        detailView.minSize = NSSize(width: 0, height: 0)
        detailView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        detailView.textContainerInset = NSSize(width: 4, height: 6)
        detailScroll.hasVerticalScroller = true
        detailScroll.borderType = .bezelBorder
        detailScroll.documentView = detailView
        detailView.textContainer?.widthTracksTextView = true
        detailView.textContainer?.containerSize = NSSize(width: detailScroll.contentSize.width, height: CGFloat.greatestFiniteMagnitude)
        spinner.style = .spinning
        spinner.controlSize = .regular
        retryButton.target = self
        retryButton.action = #selector(retry)
        retryButton.isHidden = true
        openLibraryButton.target = self
        openLibraryButton.action = #selector(openLibrary)
        openLibraryButton.isHidden = true
        logButton.target = self
        logButton.action = #selector(openLog)
        logButton.isHidden = true
        [titleLabel, detailScroll, spinner, retryButton, openLibraryButton, logButton].forEach {
            $0.translatesAutoresizingMaskIntoConstraints = false
            content.addSubview($0)
        }
        NSLayoutConstraint.activate([
            titleLabel.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            titleLabel.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            titleLabel.topAnchor.constraint(equalTo: content.topAnchor, constant: 22),
            detailScroll.leadingAnchor.constraint(equalTo: titleLabel.leadingAnchor),
            detailScroll.trailingAnchor.constraint(equalTo: titleLabel.trailingAnchor),
            detailScroll.topAnchor.constraint(equalTo: titleLabel.bottomAnchor, constant: 12),
            detailScroll.bottomAnchor.constraint(equalTo: content.bottomAnchor, constant: -64),
            spinner.leadingAnchor.constraint(equalTo: titleLabel.leadingAnchor),
            spinner.topAnchor.constraint(equalTo: detailScroll.bottomAnchor, constant: 10),
            retryButton.leadingAnchor.constraint(equalTo: titleLabel.leadingAnchor),
            retryButton.topAnchor.constraint(equalTo: detailScroll.bottomAnchor, constant: 10),
            openLibraryButton.leadingAnchor.constraint(equalTo: titleLabel.leadingAnchor),
            openLibraryButton.topAnchor.constraint(equalTo: detailScroll.bottomAnchor, constant: 10),
            logButton.leadingAnchor.constraint(equalTo: retryButton.trailingAnchor, constant: 10),
            logButton.centerYAnchor.constraint(equalTo: retryButton.centerYAnchor)
        ])
    }

    private func showSpinner(_ visible: Bool) {
        spinner.isHidden = !visible
        if visible { spinner.startAnimation(nil) } else { spinner.stopAnimation(nil) }
        retryButton.isHidden = true
        openLibraryButton.isHidden = true
        logButton.isHidden = true
    }

    private func setDetail(_ text: String) {
        detailView.string = text
        detailView.scrollToBeginningOfDocument(nil)
    }

    @objc private func retry() { coordinator.retry() }
    @objc private func openLibrary() { coordinator.openLibrary() }
    @objc private func openLog() { coordinator.revealLog() }
}
