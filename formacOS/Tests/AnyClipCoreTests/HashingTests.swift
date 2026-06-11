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
        sha256Hex(Data([0x00, 0x01, 0xff])))
    #expect(sha256Hex(Data()) ==
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
}
