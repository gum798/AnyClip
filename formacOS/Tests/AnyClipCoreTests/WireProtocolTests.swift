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
    #expect(body?["protocol_minor"] as? Int == 3)   // our live hello now advertises minor 3
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
    let files: [(name: String, data: Data, relPath: String?)] = [
        (name: "노트.txt", data: Data("one".utf8), relPath: nil),
        (name: "b.bin", data: Data([0, 1, 2]), relPath: nil),
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
    let msg = WireMessage.clipFiles(files: [(name: nfd, data: Data([1]), relPath: nil)], ts: 0)
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
    #expect(ok?[0].relPath == nil)
}

@Test func clipPayloadFilesKindAndAggregateHash() {
    let payload = ClipPayload.files([
        (name: "a", data: Data("one".utf8), relPath: nil),
        (name: "b", data: Data("two".utf8), relPath: nil),
    ])
    #expect(payload.kind == "files")
    #expect(payload.payloadHash == aggregateFilesHash([
        sha256Hex(Data("one".utf8)), sha256Hex(Data("two".utf8))]))
}

@Test func protocolMinorCoversFilesFramesAndFolderTrees() {
    // Cumulative feature level: >= 1 accepts kind:"files", >= 2 accepts frames
    // up to 64 MiB (see LargeFrameTests), >= 3 rebuilds folder trees from the
    // optional per-entry "path". Minor 3 gates NOTHING on the send path.
    #expect(Wire.protocolMinor == 3)
}

@Test func clipFilesCarriesPathOnlyForFolderEntries() throws {
    let files: [(name: String, data: Data, relPath: String?)] = [
        (name: "a.txt", data: Data("one".utf8), relPath: "docs/a.txt"),
        (name: "loose.bin", data: Data([0, 1, 2]), relPath: nil),
    ]
    let msg = WireMessage.clipFiles(files: files, ts: 5.0)
    let entries = try #require(msg.files)
    #expect(entries[0].path == "docs/a.txt")
    #expect(entries[1].path == nil)
    // A nil path is OMITTED from the JSON, so every frame that exists today
    // stays byte-identical (loose files carry no path field at all).
    let frame = try msg.encodeFrame()
    let json = try JSONSerialization.jsonObject(with: frame.dropFirst(4)) as! [String: Any]
    let raw = json["files"] as! [[String: Any]]
    #expect(raw[0]["path"] as? String == "docs/a.txt")
    #expect(raw[1]["path"] == nil)
    #expect(raw[1].keys.sorted() == ["bytes", "content", "hash", "name"])
}

@Test func clipFilesNormalizesPathToNFCAndDropsInvalidPaths() {
    let nfd = "결과".decomposedStringWithCanonicalMapping
    let nfc = "결과".precomposedStringWithCanonicalMapping
    let m1 = WireMessage.clipFiles(
        files: [(name: nfd + ".txt", data: Data([1]), relPath: nfd + "/" + nfd + ".txt")],
        ts: 0)
    #expect(Array((m1.files?[0].path ?? "").utf8) == Array((nfc + "/" + nfc + ".txt").utf8))
    // The sender MUST emit only valid paths: a traversal path degrades to a
    // flat entry instead of a frame the receiver would have to reject.
    let m2 = WireMessage.clipFiles(
        files: [(name: "a.txt", data: Data([1]), relPath: "../a.txt")], ts: 0)
    #expect(m2.files?[0].path == nil)
    #expect(m2.files?[0].name == "a.txt")
}

@Test func wirePathValidationRules() {
    #expect(isValidWirePath("docs/a.txt", name: "a.txt"))
    #expect(isValidWirePath("docs/sub dir/a.txt", name: "a.txt"))
    #expect(!isValidWirePath("/docs/a.txt", name: "a.txt"))      // absolute
    #expect(!isValidWirePath("../a.txt", name: "a.txt"))         // traversal
    #expect(!isValidWirePath("docs/./a.txt", name: "a.txt"))     // dot segment
    #expect(!isValidWirePath("docs//a.txt", name: "a.txt"))      // empty segment
    #expect(!isValidWirePath("docs\\a.txt", name: "a.txt"))      // backslash
    #expect(!isValidWirePath("C:/docs/a.txt", name: "a.txt"))    // drive letter
    #expect(!isValidWirePath("docs/a.txt", name: "b.txt"))       // last segment != name
    #expect(!isValidWirePath("", name: "a.txt"))
    #expect(!isValidWirePath(String(repeating: "d/", count: 33) + "a.txt", name: "a.txt"))
    let deep = String(repeating: "0123456789/", count: 22) + "a.txt"   // 247 scalars
    #expect(deep.unicodeScalars.count > Wire.maxPathLength)
    #expect(!isValidWirePath(deep, name: "a.txt"))
    // Length is counted in UNICODE SCALARS (== Python len()), never in
    // graphemes or UTF-16 units. This path is 136 graphemes but 266 scalars
    // (and 526 UTF-16 units): a grapheme count would ACCEPT it while Python
    // rejects it, which is exactly the silent cross-implementation split the
    // 240 cap exists to prevent. Keep this case non-ASCII.
    let flags = String(repeating: "🇰🇷", count: 130) + "/a.txt"
    #expect(flags.count <= Wire.maxPathLength)                 // graphemes: 136
    #expect(flags.unicodeScalars.count > Wire.maxPathLength)   // scalars: 266
    #expect(!isValidWirePath(flags, name: "a.txt"))
    // NFD is normalized, never rejected (see the shared decision in
    // Interfaces — Tasks 1 and 7 must match): Swift's String == is canonical
    // and every segment goes through sanitizeFilename (NFC) on the way to disk.
    let nfd = "결과".decomposedStringWithCanonicalMapping
    #expect(isValidWirePath(nfd + "/" + nfd + ".txt", name: nfd + ".txt"))
    #expect(isValidWirePath(nfd + "/" + nfd + ".txt",
                            name: "결과".precomposedStringWithCanonicalMapping + ".txt"))
}

@Test func sanitizeWirePathSanitizesEverySegment() {
    #expect(sanitizeWirePath("docs/CON/a?b.txt") == "docs/_CON/a_b.txt")
    #expect(sanitizeWirePath("docs/sub/a.txt") == "docs/sub/a.txt")
}

@Test func decodeFileEntriesCarriesPathThroughRaw() {
    let tree = WireFileEntry(
        name: "a.txt", content: Data("x".utf8).base64EncodedString(),
        hash: sha256Hex(Data("x".utf8)), bytes: 1, path: "docs/a.txt")
    #expect(decodeFileEntries([tree])?[0].relPath == "docs/a.txt")
    // No path field -> relPath nil -> exactly today's flat behavior.
    let flat = WireFileEntry(
        name: "b.txt", content: Data("y".utf8).base64EncodedString(),
        hash: sha256Hex(Data("y".utf8)), bytes: 1)
    #expect(decodeFileEntries([flat])?[0].relPath == nil)
}
