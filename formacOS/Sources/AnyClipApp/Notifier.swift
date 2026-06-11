import Foundation
import UserNotifications
import AnyClipCore
import AnyClipDaemon  // Locked

/// Desktop toasts via UserNotifications. Outside a proper .app bundle (or
/// when authorization is denied) every call is a silent no-op — calling
/// UNUserNotificationCenter without a bundle would crash.
final class Notifier: @unchecked Sendable {
    private let enabled = Locked(false)

    func setup() {
        guard Bundle.main.bundleIdentifier != nil else {
            AnyLog.shared.warning("not running from a bundle; notifications disabled")
            return
        }
        UNUserNotificationCenter.current().requestAuthorization(
            options: [.alert]) { [enabled] granted, _ in
            enabled.set(granted)
            if !granted {
                AnyLog.shared.warning("notification permission denied; toasts disabled")
            }
        }
    }

    func notify(title: String, body: String) {
        guard enabled.get() else { return }
        let content = UNMutableNotificationContent()
        content.title = title
        content.body = String(body.prefix(240))
        let request = UNNotificationRequest(
            identifier: UUID().uuidString, content: content, trigger: nil)
        UNUserNotificationCenter.current().add(request)
    }
}
