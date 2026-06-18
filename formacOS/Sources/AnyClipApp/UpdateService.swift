import Foundation
import AppKit
import AnyClipCore

/// Real network + process side of updates. Keeps StatusItemController free of IO.
@MainActor
final class UpdateService {
    private let appVersion: String
    init(appVersion: String) { self.appVersion = appVersion }

    func check() async -> UpdateStatus {
        await UpdateChecker.checkForUpdate(current: appVersion) {
            try await Self.fetchLatestJSON()
        }
    }

    private static func fetchLatestJSON() async throws -> String {
        var req = URLRequest(url: URL(string: UpdateChecker.releasesApiURL)!)
        req.setValue("AnyClip-updater", forHTTPHeaderField: "User-Agent")
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.timeoutInterval = 8
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse, http.statusCode == 200 else {
            throw NSError(domain: "AnyClip", code: 1,
                userInfo: [NSLocalizedDescriptionKey: "GitHub returned a non-200 status"])
        }
        return String(decoding: data, as: UTF8.self)
    }

    /// Spawn the detached upgrade helper, then the caller quits. The helper
    /// outlives us (reparented to launchd) and relaunches the new app.
    func installAndRelaunch() {
        let script = UpdateCommand.macHelperScript(
            pid: ProcessInfo.processInfo.processIdentifier,
            brewPath: Self.locateBrew(), appName: "AnyClip",
            releasesURL: UpdateChecker.releasesPageURL)
        let proc = Process()
        proc.executableURL = URL(fileURLWithPath: "/bin/sh")
        proc.arguments = ["-c", script]
        do { try proc.run() }
        catch { openReleasesPage() }  // could not spawn helper → manual path
    }

    func openReleasesPage() {
        if let url = URL(string: UpdateChecker.releasesPageURL) {
            NSWorkspace.shared.open(url)
        }
    }

    private static func locateBrew() -> String {
        for p in ["/opt/homebrew/bin/brew", "/usr/local/bin/brew"]
        where FileManager.default.isExecutableFile(atPath: p) { return p }
        return "brew"
    }
}
