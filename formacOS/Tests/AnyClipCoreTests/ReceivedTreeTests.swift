import Testing
import Foundation
@testable import AnyClipCore

private func entry(_ name: String, _ rel: String?) -> (name: String, data: Data, relPath: String?) {
    (name: name, data: Data(name.utf8), relPath: rel)
}

@Test func planRebuildsATreeAndKeepsLooseFilesFlat() {
    let got = ReceivedTree.plan([
        entry("a.txt", "docs/a.txt"),
        entry("b.txt", "docs/sub/b.txt"),
        entry("loose.txt", nil),
    ], exists: { _ in false })
    #expect(got.map(\.relativePath) == ["docs/a.txt", "docs/sub/b.txt", "loose.txt"])
    #expect(got.map(\.top) == ["docs", "docs", "loose.txt"])
    #expect(got.map(\.inTree) == [true, true, false])
}

@Test func planFallsBackToFlatOnEveryPathViolation() {
    let bad = [
        entry("evil.txt", "../../evil.txt"),      // traversal
        entry("abs.txt", "/etc/abs.txt"),         // absolute
        entry("win.txt", "C:\\tmp\\win.txt"),     // drive letter + backslashes
        entry("lie.txt", "docs/other.txt"),       // last segment != name
        entry("empty.txt", "docs//empty.txt"),    // empty segment
        entry("deep.txt", String(repeating: "d/", count: 33) + "deep.txt"),
    ]
    let got = ReceivedTree.plan(bad, exists: { _ in false })
    #expect(got.map(\.relativePath)
        == ["evil.txt", "abs.txt", "win.txt", "lie.txt", "empty.txt", "deep.txt"])
    #expect(got.allSatisfy { !$0.inTree })
}

@Test func planTreatsASingleSegmentPathAsALooseFile() {
    let got = ReceivedTree.plan([entry("a.txt", "a.txt")], exists: { _ in false })
    #expect(got == [TreePlacement(relativePath: "a.txt", top: "a.txt", inTree: false)])
}

@Test func planUniquifiesTheTopOnceForTheWholeClip() {
    let got = ReceivedTree.plan([
        entry("a.txt", "docs/a.txt"),
        entry("b.txt", "docs/sub/b.txt"),
        entry("c.txt", "notes/c.txt"),
    ], exists: { $0 == "docs" })
    // ONE clip lands in ONE new folder: every entry under "docs" moves together.
    #expect(got.map(\.relativePath) == ["docs-2/a.txt", "docs-2/sub/b.txt", "notes/c.txt"])
    #expect(got.map(\.top) == ["docs-2", "docs-2", "notes"])
}

@Test func planBumpsThroughSuccessiveTopCollisions() {
    let got = ReceivedTree.plan([entry("a.txt", "docs/a.txt")],
                                exists: { ["docs", "docs-2", "docs-3"].contains($0) })
    #expect(got[0].relativePath == "docs-4/a.txt")
}

@Test func planKeepsLooseNamesOffTheReservedTops() {
    let got = ReceivedTree.plan([
        entry("a.txt", "docs/a.txt"),
        entry("docs", nil),          // a loose file literally named "docs"
        entry("docs", nil),
    ], exists: { _ in false })
    #expect(got.map(\.relativePath) == ["docs/a.txt", "docs (2)", "docs (3)"])
}

@Test func planKeepsLooseNamesOffWhatIsAlreadyInReceived() {
    // received/ holds TREES now: a loose file named like a folder already
    // sitting there must be bumped, never planned straight onto the directory.
    // anyclip.plan_received_layout uniquifies the loose names against
    // `sorted(existing) + names`, which is what `alsoTaken` reproduces here.
    let got = ReceivedTree.plan([entry("docs", nil), entry("docs", nil)],
                                exists: { $0 == "docs" })
    #expect(got.map(\.relativePath) == ["docs (2)", "docs (3)"])
    #expect(got.allSatisfy { !$0.inTree })
}

@Test func planSanitizesEverySegmentAndNormalizesToNFC() {
    let nfd = "결과".decomposedStringWithCanonicalMapping
    let nfc = "결과".precomposedStringWithCanonicalMapping
    let got = ReceivedTree.plan([
        entry(nfd + ".txt", nfd + "/" + nfd + ".txt"),
        entry("q?.txt", "docs/CON/q?.txt"),
    ], exists: { _ in false })
    // NFC is a REJECTION rule on the wire, not a normalization: a decomposed
    // path is refused outright (isValidWirePath / anyclip.is_valid_wire_path,
    // pinned by tests/test_wire_files.py::test_only_nfc_paths_are_accepted_on
    // _the_wire), so this entry lands FLAT — under a name the per-name
    // sanitizer has composed. Byte comparison, because String == is canonical.
    #expect(Array(got[0].relativePath.utf8) == Array((nfc + ".txt").utf8))
    #expect(got[0].inTree == false)
    // A VALID path keeps its tree and every segment goes through the per-name
    // sanitizer (Windows reserved device name, denied character).
    #expect(got[1].relativePath == "docs/_CON/q_.txt")
    #expect(got[1].inTree)
}
