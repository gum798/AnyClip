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
    #expect(sanitizeFilename("a/b/c.txt") == "c.txt")          // basename only
    // Denylist (not whitelist): "!" and "&" are kept; ":" and "?" become "_".
    #expect(sanitizeFilename("we!rd:na?me") == "we!rd_na_me")
    #expect(sanitizeFilename("") == "received.bin")
    #expect(sanitizeFilename("   ") == "received.bin")
    #expect(sanitizeFilename("한글파일.txt") == "한글파일.txt")
    #expect(sanitizeFilename("???") == "___")
}

@Test func sanitizeKeepsParensAmpersandSpacesForRealFilename() {
    // The reported regression: the alnum whitelist mangled "(", "&", ")" to "_".
    let name = "(E&S)_SCM 마스터플랜_20250915_공유6.pptx"
    #expect(sanitizeFilename(name) == name)                    // survives UNCHANGED
}

@Test func sanitizeSplitsOnBothSlashKinds() {
    #expect(sanitizeFilename("a\\b\\c.txt") == "c.txt")        // Windows backslash path
    #expect(sanitizeFilename("../x") == "x")                   // traversal -> last component
    #expect(sanitizeFilename("..") == "received.bin")          // dotdot -> received.bin
    #expect(sanitizeFilename(".") == "received.bin")
}

@Test func sanitizeTrimsTrailingDotsAndSpaces() {
    #expect(sanitizeFilename("name.  ") == "name")
    #expect(sanitizeFilename("name...") == "name")
    #expect(sanitizeFilename("keep.mid.dots.txt") == "keep.mid.dots.txt")
}

@Test func sanitizePrefixesWindowsReservedDeviceNames() {
    #expect(sanitizeFilename("CON") == "_CON")
    #expect(sanitizeFilename("con.txt") == "_con.txt")         // case-insensitive, stem-before-first-dot
    #expect(sanitizeFilename("COM1") == "_COM1")
    #expect(sanitizeFilename("LPT9.log") == "_LPT9.log")
    #expect(sanitizeFilename("COM10") == "COM10")              // not a reserved device
    #expect(sanitizeFilename("console.txt") == "console.txt")  // only exact stem matches
}

@Test func uniquifyInsertsSuffixBeforeLastExtension() {
    #expect(uniquifyNames(["a.txt", "a.txt", "a.txt"]) == ["a.txt", "a (2).txt", "a (3).txt"])
    #expect(uniquifyNames(["x", "x"]) == ["x", "x (2)"])       // no extension
    #expect(uniquifyNames(["a.tar.gz", "a.tar.gz"]) == ["a.tar.gz", "a.tar (2).gz"]) // last ext only
    #expect(uniquifyNames(["a.txt", "b.txt"]) == ["a.txt", "b.txt"]) // no collision -> untouched
    #expect(uniquifyNames([".env", ".env"]) == [".env", ".env (2)"]) // leading dot != extension
    #expect(uniquifyNames(["a (2).txt", "a.txt", "a.txt"]) == ["a (2).txt", "a.txt", "a (3).txt"]) // guard vs existing
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
