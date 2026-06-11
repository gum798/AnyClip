import AppKit
import AnyClipCore
import AnyClipDaemon

/// Menu bar shell. Icon reflects link state; all state lives in the dropdown.
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

        // Set initial icon via the same path used by apply(_:).
        updateIcon(.initial)
    }

    func apply(_ state: PeerUIState) {
        updateIcon(state)
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

    // ---- icon -----------------------------------------------------------

    private func updateIcon(_ state: PeerUIState) {
        let spec = menuIconSpec(for: state)
        guard let button = statusItem.button else { return }
        if spec.highlighted {
            button.attributedTitle = NSAttributedString(
                string: spec.text,
                attributes: [
                    .foregroundColor: NSColor.systemRed,
                    .font: NSFont.menuBarFont(ofSize: 0),
                ])
        } else {
            button.attributedTitle = NSAttributedString(
                string: spec.text,
                attributes: [.font: NSFont.menuBarFont(ofSize: 0)])
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
            + "token before sync resumes.\n\n"
            + "Enter token… lets you paste the token from your other device."
        alert.addButton(withTitle: "Close")       // first = default (Return key)
        alert.addButton(withTitle: "Enter token…") // second
        alert.addButton(withTitle: "Reset…")       // third

        switch alert.runModal() {
        case .alertSecondButtonReturn:
            // "Enter token…" — prompt the user for a known token
            enterTokenFlow()
        case .alertThirdButtonReturn:
            // "Reset…" — existing two-step confirm + done + quit
            resetTokenFlow()
        default:
            return  // "Close"
        }
    }

    /// Prompt the user to paste a token from another device, save it, then quit.
    private func enterTokenFlow() {
        let prompt = NSAlert()
        prompt.messageText = "Enter shared token"
        prompt.informativeText = "Paste the token shown on your other device."
        prompt.addButton(withTitle: "OK")
        prompt.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        prompt.accessoryView = field
        prompt.window.initialFirstResponder = field
        guard prompt.runModal() == .alertFirstButtonReturn else { return }
        let newToken = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !newToken.isEmpty else { return }

        do {
            try ConfigStore.save(StoredConfig(token: newToken))
        } catch {
            let failed = NSAlert()
            failed.messageText = "Could not save token"
            failed.informativeText = "Saving to \(ConfigStore.configPath().path) failed: \(error.localizedDescription)"
            failed.runModal()
            return
        }
        let done = NSAlert()
        done.messageText = "Token saved"
        done.informativeText =
            "AnyClip will now quit. Relaunch to apply, then make sure "
            + "your other device uses the same token."
        done.runModal()
        quit()
    }

    /// Generate a fresh random token, confirm with the user, save, then quit.
    private func resetTokenFlow() {
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
        do {
            try ConfigStore.save(StoredConfig(token: newToken))
        } catch {
            let failed = NSAlert()
            failed.messageText = "Could not save token"
            failed.informativeText = "Saving to \(ConfigStore.configPath().path) failed: \(error.localizedDescription)"
            failed.runModal()
            return
        }
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
            do {
                try autostart.enable(executablePath: exe)
                startAtLoginItem.state = .on
            } catch {
                let alert = NSAlert()
                alert.messageText = "Could not enable Start at Login"
                alert.informativeText = error.localizedDescription
                alert.runModal()
            }
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
