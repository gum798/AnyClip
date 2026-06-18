import Testing
@testable import AnyClipCore

@Test func macHelperScriptHasAllPieces() {
    let s = UpdateCommand.macHelperScript(
        pid: 4242, brewPath: "/opt/homebrew/bin/brew",
        appName: "AnyClip", releasesURL: "https://example.test/r")
    #expect(s.contains("kill -0 4242"))                        // waits for our exit
    #expect(s.contains("/opt/homebrew/bin/brew upgrade --cask anyclip"))
    #expect(s.contains(#"/usr/bin/open -a "AnyClip""#))        // relaunch on success
    #expect(s.contains(#"/usr/bin/open "https://example.test/r""#)) // fallback on failure
}
