import Foundation
import AnyClipCore

/// LaunchAgent registration. Deliberately uses the SAME label/path as the
/// Python app (com.anyclip) so a migrating user never ends up with two
/// autostart entries fighting over port 24816. Port of autostart.MacAutostart.
public struct Autostart {
    public static let label = "com.anyclip"

    let homeDir: URL
    let runLaunchctl: Bool

    public init(
        homeDir: URL = FileManager.default.homeDirectoryForCurrentUser,
        runLaunchctl: Bool = true
    ) {
        self.homeDir = homeDir
        self.runLaunchctl = runLaunchctl
    }

    public var plistPath: URL {
        homeDir.appendingPathComponent(
            "Library/LaunchAgents/\(Self.label).plist")
    }

    public func isEnabled() -> Bool {
        FileManager.default.fileExists(atPath: plistPath.path)
    }

    public func enable(executablePath: String) throws {
        let logDir = homeDir.appendingPathComponent(".anyclip")
        try FileManager.default.createDirectory(
            at: logDir, withIntermediateDirectories: true)
        let plist: [String: Any] = [
            "Label": Self.label,
            "ProgramArguments": [executablePath],
            "RunAtLoad": true,
            "KeepAlive": true,
            // launchd does not expand ~ -- absolute paths only.
            "StandardOutPath": logDir.appendingPathComponent("launchd.stdout.log").path,
            "StandardErrorPath": logDir.appendingPathComponent("launchd.stderr.log").path,
        ]
        try FileManager.default.createDirectory(
            at: plistPath.deletingLastPathComponent(), withIntermediateDirectories: true)
        let data = try PropertyListSerialization.data(
            fromPropertyList: plist, format: .xml, options: 0)
        try data.write(to: plistPath)
        if runLaunchctl {
            // Unload first in case we are overwriting -- launchctl refuses
            // to load an already-registered label.
            launchctl(["unload", plistPath.path])
            launchctl(["load", plistPath.path])
        }
    }

    public func disable() {
        if isEnabled(), runLaunchctl {
            launchctl(["unload", plistPath.path])
        }
        try? FileManager.default.removeItem(at: plistPath)
    }

    private func launchctl(_ args: [String]) {
        let process = Process()
        process.executableURL = URL(fileURLWithPath: "/bin/launchctl")
        process.arguments = args
        process.standardOutput = FileHandle.nullDevice
        process.standardError = FileHandle.nullDevice
        do {
            try process.run()
            process.waitUntilExit()
        } catch {
            AnyLog.shared.warning("launchctl \(args) failed: \(error)")
        }
    }
}
