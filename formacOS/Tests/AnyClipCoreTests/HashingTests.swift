import Testing
import Foundation
@testable import AnyClipCore

@Test func sha256HexOfString() {
    // python: hashlib.sha256("hello".encode()).hexdigest()
    #expect(sha256Hex("hello") ==
        "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")
}

@Test func sha256HexOfUnicodeString() {
    // python3 -c 'import hashlib; print(hashlib.sha256("안녕".encode()).hexdigest())'
    #expect(sha256Hex("안녕") ==
        "e8f817f346d1d411cc59d5bdda64fab3763890e1f0f8f4c15805cf78874d68bf")
}

@Test func sha256HexOfBytes() {
    #expect(sha256Hex(Data([0x00, 0x01, 0xff])) ==
        "26a66b061e8f48f39927c312f25293959729eee95978e2892d49d3512a5cc092")
    #expect(sha256Hex(Data()) ==
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
}

@Test func aggregateFilesHashMatchesFormulaAndIsOrderIndependent() {
    // Aggregate = sha256 of the per-file hex hashes sorted lexicographically
    // and concatenated with no separator. Hex is ASCII, so "a…" sorts before
    // "b…" — assert against the formula itself (no magic constant).
    let h1 = String(repeating: "a", count: 64)
    let h2 = String(repeating: "b", count: 64)
    let expected = sha256Hex(h1 + h2)
    #expect(aggregateFilesHash([h1, h2]) == expected)
    #expect(aggregateFilesHash([h2, h1]) == expected) // input order must not matter
    #expect(aggregateFilesHash(["ff", "00"]) == sha256Hex("00" + "ff"))
}
