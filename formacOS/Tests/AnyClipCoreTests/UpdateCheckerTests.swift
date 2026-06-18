import Testing
import Foundation
@testable import AnyClipCore

@Test func compareVersionsOrders() {
    #expect(UpdateChecker.compareVersions("1.1.6", "1.1.7") == .orderedAscending)
    #expect(UpdateChecker.compareVersions("1.1.7", "1.1.7") == .orderedSame)
    #expect(UpdateChecker.compareVersions("1.2.0", "1.1.9") == .orderedDescending)
    #expect(UpdateChecker.compareVersions("1.1.10", "1.1.9") == .orderedDescending) // numeric, not lexical
    #expect(UpdateChecker.compareVersions("1.1.8-beta", "1.1.8") == .orderedAscending) // pre-release lower
    #expect(UpdateChecker.compareVersions("0.0.0-dev", "1.1.7") == .orderedAscending) // dev lowest
}

@Test func parseLatestTagStripsV() {
    #expect(UpdateChecker.parseLatestTag(#"{"tag_name":"v1.1.7","name":"x"}"#) == "1.1.7")
    #expect(UpdateChecker.parseLatestTag(#"{"tag_name":"1.2.0"}"#) == "1.2.0")
    #expect(UpdateChecker.parseLatestTag(#"{"no_tag":true}"#) == nil)
    #expect(UpdateChecker.parseLatestTag("not json") == nil)
}

@Test func checkForUpdateClassifies() async {
    let newer = await UpdateChecker.checkForUpdate(current: "1.1.6") { #"{"tag_name":"v1.1.7"}"# }
    #expect(newer == .available(latest: "1.1.7", url: UpdateChecker.releasesPageURL))
    let same = await UpdateChecker.checkForUpdate(current: "1.1.7") { #"{"tag_name":"v1.1.7"}"# }
    #expect(same == .upToDate(current: "1.1.7"))
    let bad = await UpdateChecker.checkForUpdate(current: "1.1.7") { "garbage" }
    if case .failed = bad {} else { Issue.record("expected .failed for unparseable body") }
    struct Boom: Error {}
    let threw = await UpdateChecker.checkForUpdate(current: "1.1.7") { throw Boom() }
    if case .failed = threw {} else { Issue.record("expected .failed when fetch throws") }
}
