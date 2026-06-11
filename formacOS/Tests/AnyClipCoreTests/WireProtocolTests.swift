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
    #expect(body?["protocol_minor"] as? Int == 0)
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
    #expect(throws: WireFrameError.self) { try big.encodeFrame() }
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
}
