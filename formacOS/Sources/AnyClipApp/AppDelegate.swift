import AppKit
import AnyClipCore
import AnyClipDaemon

// @MainActor is omitted here: NSApplicationDelegate callbacks are always
// dispatched on the main thread by AppKit, and Swift 5 mode does not allow
// constructing a @MainActor class from nonisolated top-level main.swift code.
// Individual methods that need MainActor isolation use assumeIsolated or are
// called directly from AppKit callbacks (already on main thread).
final class AppDelegate: NSObject, NSApplicationDelegate {
    private var controller: StatusItemController?
    private var daemon: Daemon?
    private var daemonTask: Task<Void, Never>?
    private let notifier = Notifier()

    func applicationDidFinishLaunching(_ notification: Notification) {
        let stateDir = FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true)
        let logURL = stateDir.appendingPathComponent("anyclip.log")
        AnyLog.shared.configure(fileURL: logURL, verbose: false)

        guard let token = Onboarding.resolveToken() else {
            FileHandle.standardError.write(
                Data("anyclip: onboarding cancelled, exiting\n".utf8))
            NSApp.terminate(nil)
            return
        }
        // Permission prompt is deferred: only request if toasts are already
        // enabled; otherwise the Notifications menu toggle triggers setup().
        if NotificationSettings.enabled { notifier.setup() }

        let appVersion = (Bundle.main.infoDictionary?["CFBundleShortVersionString"]
            as? String)
            ?? ProcessInfo.processInfo.environment["ANYCLIP_BUILD_VERSION"]
            ?? "0.0.0-dev"

        let config = DaemonConfig(token: token)
        let daemon = Daemon(
            config: config, appVersion: appVersion,
            notifier: { [notifier, weak self] title, body in
                // Sync toasts carry an arrow ("AnyClip ← peer" / "AnyClip →
                // peer"); the folder-skip toast ("AnyClip") does not and must
                // not pulse the glyph.
                if title.contains("←") || title.contains("→") {
                    Task { @MainActor in self?.controller?.animateSyncPulse() }
                }
                if NotificationSettings.enabled {
                    notifier.notify(title: title, body: body)
                }
            },
            onFatal: { message in
                Task { @MainActor in
                    let alert = NSAlert()
                    alert.messageText = "AnyClip cannot start"
                    alert.informativeText = message
                    alert.runModal()
                    NSApp.terminate(nil)
                }
            })
        self.daemon = daemon

        controller = StatusItemController(
            logFileURL: logURL,
            onNotificationsEnabled: { [notifier] in notifier.setup() },
            onQuit: { [weak self] in self?.quitGracefully() })

        daemonTask = Task { await daemon.runForever() }

        // Fold daemon events into UI state on the main actor.
        Task { @MainActor [weak self] in
            guard let events = self?.daemon?.events else { return }
            var state = PeerUIState.initial
            self?.controller?.apply(state)
            for await event in events {
                state = reducePeerState(
                    state, event, now: Date().timeIntervalSince1970)
                self?.controller?.apply(state)
            }
        }
    }

    private func quitGracefully() {
        let task = daemonTask
        task?.cancel()
        Task {
            // Give cleanup (mDNS unregister, pid release) up to 3 s,
            // matching the Python supervisor.stop(timeout=3).
            await withTaskGroup(of: Void.self) { group in
                group.addTask { await task?.value }
                group.addTask {
                    try? await Task.sleep(nanoseconds: 3_000_000_000)
                }
                await group.next()
                group.cancelAll()
            }
            await MainActor.run { NSApp.terminate(nil) }
        }
    }
}
