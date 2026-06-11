import AppKit
import AnyClipCore

/// First-launch token dialog. Port of app/onboarding.py + the resolution
/// order from menubar_mac._resolve_token: env > on-disk config > dialog.
@MainActor
enum Onboarding {
    static func resolveToken() -> String? {
        if let env = ProcessInfo.processInfo.environment["ANYCLIP_TOKEN"],
           !env.isEmpty {
            return env
        }
        if let stored = ConfigStore.load() {
            return stored.token
        }
        guard let token = show() else { return nil }
        try? ConfigStore.save(StoredConfig(token: token))
        return token
    }

    private static func show() -> String? {
        let alert = NSAlert()
        alert.messageText = "Welcome to AnyClip"
        alert.informativeText =
            "Choose how to set the shared clipboard token. Both devices "
            + "must use the same value."
        alert.addButton(withTitle: "Generate new token (first device)")
        alert.addButton(withTitle: "Enter existing token (second device)")
        alert.addButton(withTitle: "Cancel")
        switch alert.runModal() {
        case .alertFirstButtonReturn:
            return ConfigStore.generateToken()
        case .alertSecondButtonReturn:
            return promptForToken()
        default:
            return nil
        }
    }

    private static func promptForToken() -> String? {
        let alert = NSAlert()
        alert.messageText = "Enter shared token"
        alert.informativeText = "Paste the token shown on your first device."
        alert.addButton(withTitle: "OK")
        alert.addButton(withTitle: "Cancel")
        let field = NSTextField(frame: NSRect(x: 0, y: 0, width: 320, height: 24))
        alert.accessoryView = field
        alert.window.initialFirstResponder = field
        guard alert.runModal() == .alertFirstButtonReturn else { return nil }
        let value = field.stringValue.trimmingCharacters(in: .whitespacesAndNewlines)
        return value.isEmpty ? nil : value
    }
}
