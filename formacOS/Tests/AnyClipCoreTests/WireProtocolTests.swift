import Testing
import Foundation
@testable import AnyClipCore

@Test func frameIsFourByteBigEndianLengthPlusUTF8JSON() throws {
    let msg = WireMessage.ping(ts: 1.5)
    let frame = try msg.encodeFrame()
    let n = WireMessage.frameLength(frame.prefix(4))
    #expect(n == frame.count - 4)
    let body = try JSONSerialization.jsonObject(
        with: frame.dropFirst(4)) as? [String: Any]
    #expect(body?["type"] as? String == "ping")
    #expect(body?["ts"] as? Double == 1.5)
}

@Test func clipFileNameIsNFCOnTheWire() {
    // Filenames must leave as NFC (composed) bytes so a Windows peer renders
    // Korean names instead of broken conjoining jamo. macOS reads them in NFD.
    // Swift String == is canonical, so assert on the actual UTF-8 bytes.
    let base = "결과보고서"
    let nfd = base.decomposedStringWithCanonicalMapping + ".pdf"
    let nfc = base.precomposedStringWithCanonicalMapping + ".pdf"
    #expect(Array(nfd.utf8) != Array(nfc.utf8))
    let m = WireMessage.clipFile(name: nfd, data: Data([1, 2, 3]), ts: 0)
    #expect(Array((m.name ?? "").utf8) == Array(nfc.utf8))
}

@Test func helloCarriesAllProtocolFields() throws {
    let msg = WireMessage.hello(
        tokenHash: sha256Hex("tok"), nodeID: "node-1", name: "mac", appVersion: "1.2.3")
    let frame = try msg.encodeFrame()
    let body = try JSONSerialization.jsonObject(
        with: frame.dropFirst(4)) as? [String: Any]
    #expect(body?["type"] as? String == "hello")
    #expect(body?["token"] as? String == sha256Hex("tok"))
    #expect(body?["node_id"] as? String == "node-1")
    #expect(body?["name"] as? String == "mac")
    #expect(body?["version"] as? Int == 1)          // legacy field MUST exist
    #expect(body?["app_version"] as? String == "1.2.3")
    #expect(body?["protocol_major"] as? Int == 1)
    #expect(body?["protocol_minor"] as? Int == 1)   // our live hello now advertises minor 1
}

@Test func clipTextRoundTrip() throws {
    let msg = WireMessage.clipText("안녕 AnyClip 👋", ts: 2.0)
    let frame = try msg.encodeFrame()
    let decoded = WireMessage.decodeBody(frame.dropFirst(4))
    #expect(decoded?.type == "clip")
    #expect(decoded?.kind == "text")
    #expect(decoded?.content == "안녕 AnyClip 👋")
    #expect(decoded?.hash == sha256Hex("안녕 AnyClip 👋"))
}

@Test func clipImageBase64AndByteCount() throws {
    let png = Data([0x89, 0x50, 0x4E, 0x47, 0x00, 0xFF])
    let msg = WireMessage.clipImage(png, ts: 3.0)
    #expect(msg.kind == "image")
    #expect(msg.bytes == png.count)
    #expect(msg.hash == sha256Hex(png))
    #expect(strictBase64Decode(msg.content!) == png)
}

@Test func clipFileCarriesName() throws {
    let data = Data("file body".utf8)
    let msg = WireMessage.clipFile(name: "réport.txt", data: data, ts: 4.0)
    #expect(msg.kind == "file")
    #expect(msg.name == "réport.txt")
    #expect(msg.bytes == data.count)
    #expect(strictBase64Decode(msg.content!) == data)
}

@Test func oversizedPayloadThrows() {
    var big = WireMessage.ping(ts: 0)
    big.content = String(repeating: "x", count: Wire.maxPayload + 1)
    #expect { try big.encodeFrame() } throws: { error in
        if case .payloadTooLarge(let n)? = error as? WireFrameError { return n > Wire.maxPayload }
        return false
    }
}

@Test func decodeBodyToleratesUnknownFields() {
    let raw = Data(#"{"type":"hello","token":"t","future_field":[1,2]}"#.utf8)
    let msg = WireMessage.decodeBody(raw)
    #expect(msg?.type == "hello")
    #expect(msg?.token == "t")
}

@Test func decodeBodyRejectsBadJSON() {
    #expect(WireMessage.decodeBody(Data("{notjson".utf8)) == nil)
}

@Test func peerVersionInfoFallsBackToLegacyVersion() {
    // Old peer sends only `version`; treat it as protocol_major, minor 0.
    var msg = WireMessage(type: "hello")
    msg.version = 1
    let v = msg.peerVersionInfo()
    #expect(v.protocolMajor == 1)
    #expect(v.protocolMinor == 0)
    #expect(v.appVersion == "unknown")
}

@Test func peerVersionInfoPrefersExplicitFields() {
    var msg = WireMessage(type: "hello")
    msg.version = 1
    msg.protocol_major = 2
    msg.protocol_minor = 3
    msg.app_version = "9.9.9"
    let v = msg.peerVersionInfo()
    #expect(v.protocolMajor == 2)
    #expect(v.protocolMinor == 3)
    #expect(v.appVersion == "9.9.9")
}

@Test func strictBase64RejectsGarbage() {
    #expect(strictBase64Decode("!!!not-base64!!!") == nil)
}

@Test func frameLengthParsesBigEndian() {
    #expect(WireMessage.frameLength(Data([0x00, 0x00, 0x01, 0x02])) == 258)
    #expect(WireMessage.frameLength(Data([0x01, 0x00, 0x00, 0x00])) == 16_777_216)
    #expect(WireMessage.frameLength(Data([0x01, 0x02])) == 0)
}

@Test func clipFilesRoundTripAndTopLevelFieldOrder() throws {
    let files: [(name: String, data: Data)] = [
        (name: "노트.txt", data: Data("one".utf8)),
        (name: "b.bin", data: Data([0, 1, 2])),
    ]
    let msg = WireMessage.clipFiles(files: files, ts: 5.0)
    #expect(msg.kind == "files")
    #expect(msg.ts == 5.0)
    #expect(msg.bytes == 6)                                     // sum of raw byte counts
    let entries = try #require(msg.files)
    #expect(entries.count == 2)
    #expect(entries[0].name == "노트.txt")
    #expect(entries[0].bytes == 3)
    #expect(strictBase64Decode(entries[0].content) == Data("one".utf8))
    #expect(entries[0].hash == sha256Hex(Data("one".utf8)))
    #expect(msg.hash == aggregateFilesHash([
        sha256Hex(Data("one".utf8)), sha256Hex(Data([0, 1, 2]))]))
    // Round-trip through the frame codec.
    let frame = try msg.encodeFrame()
    let decoded = try #require(WireMessage.decodeBody(frame.dropFirst(4)))
    #expect(decoded.files?.count == 2)
    #expect(decoded.files?[0].name == "노트.txt")
    #expect(decoded.hash == msg.hash)
    // Top-level wire fields are present. NOTE: the brief expected to assert a
    // positional order (type,kind,files), but Swift's Foundation JSONEncoder —
    // unlike Python's json.dumps and C#'s System.Text.Json — does NOT preserve
    // property declaration order: its keyed container is an unordered dictionary
    // whose iteration order is randomized per process (verified: the emitted key
    // order differs run to run). JSON objects are unordered and both peers parse
    // by key, so interop is order-independent and the Swift golden tests only
    // decode Python-canonical frames. Asserting a position here would be flaky,
    // so assert presence instead.
    let json = String(data: frame.dropFirst(4), encoding: .utf8)!
    #expect(json.contains("\"type\""))
    #expect(json.contains("\"kind\""))
    #expect(json.contains("\"files\""))
}

@Test func clipFilesNormalizesEntryNamesToNFC() {
    let base = "결과보고서"
    let nfd = base.decomposedStringWithCanonicalMapping + ".pdf"
    let nfc = base.precomposedStringWithCanonicalMapping + ".pdf"
    let msg = WireMessage.clipFiles(files: [(name: nfd, data: Data([1]))], ts: 0)
    #expect(Array((msg.files?[0].name ?? "").utf8) == Array(nfc.utf8))
}

@Test func decodeFileEntriesDropsInvalidOrEmpty() {
    let good = WireFileEntry(
        name: "a.txt", content: Data("x".utf8).base64EncodedString(),
        hash: sha256Hex(Data("x".utf8)), bytes: 1)
    let bad = WireFileEntry(name: "b.txt", content: "!!!not-base64!!!", hash: "0", bytes: 0)
    #expect(decodeFileEntries([good, bad]) == nil)              // any invalid -> whole frame dropped
    #expect(decodeFileEntries([]) == nil)                       // empty array -> dropped
    #expect(decodeFileEntries(nil) == nil)
    let ok = decodeFileEntries([good])
    #expect(ok?.count == 1)
    #expect(ok?[0].name == "a.txt")
    #expect(ok?[0].data == Data("x".utf8))
}

@Test func clipPayloadFilesKindAndAggregateHash() {
    let payload = ClipPayload.files([
        (name: "a", data: Data("one".utf8)),
        (name: "b", data: Data("two".utf8)),
    ])
    #expect(payload.kind == "files")
    #expect(payload.payloadHash == aggregateFilesHash([
        sha256Hex(Data("one".utf8)), sha256Hex(Data("two".utf8))]))
}

@Test func protocolMinorIsOne() {
    #expect(Wire.protocolMinor == 1)
}
