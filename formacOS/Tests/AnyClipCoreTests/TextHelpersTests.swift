import Testing
@testable import AnyClipCore

@Test func previewCollapsesNewlinesAndTruncates() {
    #expect(preview("a\nb\rc") == "a b c")
    #expect(preview("") == "(empty)")
    let long = String(repeating: "x", count: 100)
    #expect(preview(long) == String(repeating: "x", count: 80) + "...")
}

@Test func sanitizeKeepsSafeChars() {
    #expect(sanitizeFilename("report v2.txt") == "report v2.txt")
    #expect(sanitizeFilename("a/b/c.txt") == "c.txt")        // basename only
    #expect(sanitizeFilename("we!rd:na?me") == "we_rd_na_me")
    #expect(sanitizeFilename("") == "received.bin")
    #expect(sanitizeFilename("   ") == "received.bin")
    #expect(sanitizeFilename("한글파일.txt") == "한글파일.txt") // unicode alnum kept
}
