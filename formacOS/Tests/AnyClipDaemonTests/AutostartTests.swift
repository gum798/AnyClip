import Testing
import Foundation
@testable import AnyClipDaemon

private func tempHome() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-home-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func enableWritesLaunchAgentPlist() throws {
    let home = tempHome()
    let auto = Autostart(homeDir: home, runLaunchctl: false)
    #expect(!auto.isEnabled())
    try auto.enable(executablePath: "/Applications/AnyClip.app/Contents/MacOS/AnyClip")
    #expect(auto.isEnabled())

    let data = try Data(contentsOf: auto.plistPath)
    let plist = try PropertyListSerialization.propertyList(
        from: data, format: nil) as! [String: Any]
    #expect(plist["Label"] as? String == "com.anyclip")
    #expect(plist["ProgramArguments"] as? [String] ==
        ["/Applications/AnyClip.app/Contents/MacOS/AnyClip"])
    #expect(plist["RunAtLoad"] as? Bool == true)
    #expect(plist["KeepAlive"] as? Bool == true)
    let stdout = plist["StandardOutPath"] as? String
    #expect(stdout?.hasSuffix(".anyclip/launchd.stdout.log") == true)
}

@Test func plistPathUsesSharedPythonLabel() {
    let auto = Autostart(homeDir: tempHome(), runLaunchctl: false)
    #expect(auto.plistPath.path.hasSuffix("Library/LaunchAgents/com.anyclip.plist"))
}

@Test func disableRemovesPlist() throws {
    let home = tempHome()
    let auto = Autostart(homeDir: home, runLaunchctl: false)
    try auto.enable(executablePath: "/x/AnyClip")
    auto.disable()
    #expect(!auto.isEnabled())
    auto.disable() // idempotent
}
