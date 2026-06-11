import Testing
import Foundation
@testable import AnyClipCore

@Test func txtRoundTrip() {
    let entries: [(String, String)] = [
        ("id", "11111111-2222-3333-4444-555555555555"),
        ("version", "1"),
        ("app_version", "0.0.0-dev"),
        ("protocol_major", "1"),
        ("protocol_minor", "0"),
    ]
    let data = TXTCodec.encode(entries)
    let decoded = TXTCodec.decode(data)
    #expect(decoded["id"] == "11111111-2222-3333-4444-555555555555")
    #expect(decoded["protocol_major"] == "1")
    #expect(decoded.count == 5)
}

@Test func txtEntriesAreLengthPrefixedKeyEqualsValue() {
    // DNS TXT wire format: 1 length byte, then "key=value" bytes.
    let data = TXTCodec.encode([("k", "v")])
    #expect(data == Data([3]) + Data("k=v".utf8))
}

@Test func txtDecodeIgnoresMalformedTail() {
    var data = TXTCodec.encode([("a", "1")])
    data.append(250) // length byte promising more than available
    data.append(Data("xx".utf8))
    #expect(TXTCodec.decode(data) == ["a": "1"])
}

@Test func txtSkipsOversizedEntries() {
    let big = String(repeating: "v", count: 300)
    let data = TXTCodec.encode([("big", big), ("ok", "1")])
    #expect(TXTCodec.decode(data) == ["ok": "1"])
}
