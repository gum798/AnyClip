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
    #expect(sanitizeFilename("???") == "___")
}

@Test func sanitizeNormalizesDecomposedUnicodeToNFC() {
    // macOS hands us filenames in NFD (decomposed Hangul = conjoining jamo
    // U+11xx). Windows can't render those, so a Korean name arrives as broken
    // glyphs. Regression: normalize to NFC so a received file lands with the
    // correct name on every platform. (Mac → Windows filename corruption.)
    //
    // NOTE: Swift String == is canonical (NFD == NFC), so it can't see the
    // difference. Assert on the raw scalars — the bytes that hit the wire and
    // the filesystem, and what a Windows font actually has to render.
    let base = "KPI후보_2x2_매트릭스_v2_결과지"
    let nfd = base.decomposedStringWithCanonicalMapping + ".pdf"
    let nfc = base.precomposedStringWithCanonicalMapping + ".pdf"
    let scalars: (String) -> [UInt32] = { $0.unicodeScalars.map(\.value) }
    #expect(scalars(nfd) != scalars(nfc))                   // forms genuinely differ
    #expect(scalars(sanitizeFilename(nfd)) == scalars(nfc)) // decomposed in → composed out
    #expect(scalars(sanitizeFilename(nfc)) == scalars(nfc)) // idempotent on NFC
}
