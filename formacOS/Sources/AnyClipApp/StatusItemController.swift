import AppKit
import AnyClipCore
import AnyClipDaemon

/// Menu bar shell. Static "@" title; all state lives in the dropdown.
/// Port of app/menubar_mac.AnyClipMenubarApp (minus Sparkle).
@MainActor
final class StatusItemController: NSObject {
    private static let lanSettingsURL =
        "x-apple.systempreferences:com.apple.preference.security"
        + "?Privacy_LocalNetwork"

    private let statusItem: NSStatusItem
    private let menu = NSMenu()
    private let statusMenuItem = NSMenuItem(
        title: "Status: Idle", action: nil, keyEquivalent: "")
    private let lastSyncItem = NSMenuItem(
        title: "Last sync: —", action: nil, keyEquivalent: "")
    private var lanSettingsItem: NSMenuItem?
    private let startAtLoginItem: NSMenuItem
    private let autostart = Autostart()
    private let logFileURL: URL
    private let onQuit: () -> Void

    init(logFileURL: URL, onQuit: @escaping () -> Void) {
        self.logFileURL = logFileURL
        self.onQuit = onQuit
        statusItem = NSStatusBar.system.statusItem(
            withLength: NSStatusItem.variableLength)
        startAtLoginItem = NSMenuItem(
            title: "Start at Login", action: nil, keyEquivalent: "")
        super.init()

        statusItem.button?.title = "@"

        statusMenuItem.isEnabled = false
        lastSyncItem.isEnabled = false
        menu.autoenablesItems = false

        let tokenItem = NSMenuItem(
            title: "Token…", action: #selector(showTokenInfo), keyEquivalent: "")
        tokenItem.target = self
        startAtLoginItem.action = #selector(toggleAutostart)
        startAtLoginItem.target = self
        startAtLoginItem.state = autostart.isEnabled() ? .on : .off
        let openLogsItem = NSMenuItem(
            title: "Open Logs", action: #selector(openLogs), keyEquivalent: "")
        openLogsItem.target = self
        let quitItem = NSMenuItem(
            title: "Quit", action: #selector(quit), keyEquivalent: "")
        quitItem.target = self

        menu.addItem(statusMenuItem)
        menu.addItem(lastSyncItem)
        menu.addItem(.separator())
        menu.addItem(tokenItem)
        menu.addItem(startAtLoginItem)
        menu.addItem(openLogsItem)
        menu.addItem(.separator())
        menu.addItem(quitItem)
        statusItem.menu = menu
    }

    func apply(_ state: PeerUIState) {
        switch state.kind {
        case .linked:
            statusMenuItem.title = "Linked: \(state.peerName ?? "peer")"
            let formatter = DateFormatter()
            formatter.dateFormat = "HH:mm:ss"
            lastSyncItem.title = "Linked since: \(formatter.string(from: Date()))"
            removeLanSettingsItem()
        case .searching:
            statusMenuItem.title = "Searching for peer"
            removeLanSettingsItem()
        case .error:
            let reason = state.reason ?? "unknown"
            statusMenuItem.title = "Error: \(reason)"
            if reason == "local_network" {
                addLanSettingsItem()
            } else {
                removeLanSettingsItem()
            }
        case .idle:
            statusMenuItem.title = "Idle"
            removeLanSettingsItem()
        }
    }

    // ---- menu actions ---------------------------------------------------

    @objc private func showTokenInfo() {
        let stored = ConfigStore.load()
        let current = stored?.token ?? "(no token configured)"
        let path = ConfigStore.configPath().path

        let alert = NSAlert()
        alert.messageText = "AnyClip token"
        alert.informativeText =
            "Current token (select to copy):\n\(current)\n\n"
            + "Stored at: \(path)\n\n"
            + "Reset generates a new random token, saves it, and quits "
            + "AnyClip. The other device must re-onboard with the new "
            + "token before sync resumes."
        alert.addButton(withTitle: "Close")
        alert.addButton(withTitle: "Reset…")
        guard alert.runModal() == .alertSecondButtonReturn else { return }

        let confirm = NSAlert()
        confirm.messageText = "Reset token?"
        confirm.informativeText =
            "This will replace the current token with a fresh one. Your "
            + "other device will stop syncing until you paste the new "
            + "token there."
        confirm.addButton(withTitle: "Reset")
        confirm.addButton(withTitle: "Cancel")
        guard confirm.runModal() == .alertFirstButtonReturn else { return }

        let newToken = ConfigStore.generateToken()
        try? ConfigStore.save(StoredConfig(token: newToken))
        let done = NSAlert()
        done.messageText = "Token reset"
        done.informativeText =
            "New token saved:\n\(newToken)\n\n"
            + "AnyClip will now quit. Relaunch to apply, then paste this "
            + "token on your other device."
        done.runModal()
        quit()
    }

    @objc private func toggleAutostart() {
        if startAtLoginItem.state == .on {
            autostart.disable()
            startAtLoginItem.state = .off
        } else {
            let exe = Bundle.main.executablePath ?? CommandLine.arguments[0]
            try? autostart.enable(executablePath: exe)
            startAtLoginItem.state = .on
        }
    }

    @objc private func openLogs() {
        NSWorkspace.shared.activateFileViewerSelecting([logFileURL])
    }

    @objc private func openLanSettings() {
        if let url = URL(string: Self.lanSettingsURL) {
            NSWorkspace.shared.open(url)
        }
    }

    private func addLanSettingsItem() {
        guard lanSettingsItem == nil else { return }
        let item = NSMenuItem(
            title: "Open Local Network Settings",
            action: #selector(openLanSettings), keyEquivalent: "")
        item.target = self
        // Insert just above "Open Logs", like the Python menu.
        let index = menu.items.firstIndex { $0.title == "Open Logs" } ?? menu.items.count
        menu.insertItem(item, at: index)
        lanSettingsItem = item
    }

    private func removeLanSettingsItem() {
        guard let item = lanSettingsItem else { return }
        menu.removeItem(item)
        lanSettingsItem = nil
    }

    @objc private func quit() {
        onQuit()
    }
}
