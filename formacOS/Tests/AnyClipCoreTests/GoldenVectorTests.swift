import Testing
import Foundation
@testable import AnyClipCore

private func fixture(_ name: String) throws -> Data {
    if let url = Bundle.module.url(forResource: name, withExtension: nil,
                                   subdirectory: "Fixtures") {
        return try Data(contentsOf: url)
    }
    // Fallback: SwiftPM sometimes flattens copied resource dirs at the bundle root.
    let url = Bundle.module.url(forResource: name, withExtension: nil)!
    return try Data(contentsOf: url)
}

private func manifest() throws -> [String: Any] {
    try JSONSerialization.jsonObject(with: fixture("manifest.json")) as! [String: Any]
}

private func decodeGoldenFrame(_ name: String) throws -> WireMessage {
    let frame = try fixture(name)
    let n = WireMessage.frameLength(frame.prefix(4))
    #expect(n == frame.count - 4)
    let msg = WireMessage.decodeBody(frame.dropFirst(4))
    #expect(msg != nil)
    return msg!
}

@Test func goldenHelloDecodes() throws {
    let m = try decodeGoldenFrame("hello.bin")
    let man = try manifest()
    #expect(m.type == "hello")
    #expect(m.token == man["token_hash"] as? String)
    #expect(m.node_id == man["node_id"] as? String)
    #expect(m.name == "golden-mac")
    #expect(m.version == 1)
    #expect(m.protocol_major == 1)
    #expect(m.protocol_minor == 0)
    // Our own hashing of the golden token must equal Python's.
    #expect(sha256Hex(man["token"] as! String) == man["token_hash"] as? String)
}

@Test func goldenClipTextDecodes() throws {
    let m = try decodeGoldenFrame("clip_text.bin")
    let man = try manifest()
    #expect(m.kind == "text")
    #expect(m.content == man["text"] as? String)
    #expect(sha256Hex(m.content!) == man["text_hash"] as? String)
}

@Test func goldenClipImageDecodes() throws {
    let m = try decodeGoldenFrame("clip_image.bin")
    let man = try manifest()
    let data = strictBase64Decode(m.content!)
    #expect(data != nil)
    #expect(sha256Hex(data!) == man["image_hash"] as? String)
    #expect(m.bytes == data!.count)
    // Our base64 encoding round-trips to Python's exact string.
    #expect(data!.base64EncodedString() == man["image_b64"] as? String)
}

@Test func goldenClipFileDecodes() throws {
    let m = try decodeGoldenFrame("clip_file.bin")
    let man = try manifest()
    #expect(m.name == man["file_name"] as? String)
    let data = strictBase64Decode(m.content!)
    #expect(sha256Hex(data!) == man["file_hash"] as? String)
}

@Test func goldenPingDecodes() throws {
    let m = try decodeGoldenFrame("ping.bin")
    #expect(m.type == "ping")
    #expect(m.ts == 1718000000.5)
}

@Test func ourHelloDecodesLikePythonWould() throws {
    // Sanity: encode our hello and re-parse it with the same tolerant rules
    // Python uses (a dict lookup). Field names must be snake_case.
    let m = WireMessage.hello(tokenHash: "h", nodeID: "n", name: "x", appVersion: "1")
    let body = try JSONEncoder().encode(m)
    let dict = try JSONSerialization.jsonObject(with: body) as! [String: Any]
    for key in ["type", "token", "node_id", "name", "version",
                "app_version", "protocol_major", "protocol_minor"] {
        #expect(dict[key] != nil, "missing wire field \(key)")
    }
}
