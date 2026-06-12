import Foundation

/// App-local notification preference. Lives in UserDefaults (NOT the shared
/// ~/.anyclip/config.json, which the Python/C# implementations rewrite with
/// only the token). Default: disabled — the menu bar sync pulse replaces
/// toasts as the default feedback.
enum NotificationSettings {
    private static let key = "showNotifications"
    static var enabled: Bool {
        get { UserDefaults.standard.bool(forKey: key) }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }
}
