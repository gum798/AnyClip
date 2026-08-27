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
    #expect(m.protocol_minor == 3)
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
    #expect(data != nil)
    #expect(sha256Hex(data!) == man["file_hash"] as? String)
    #expect(m.bytes == data!.count)
}

@Test func goldenClipFilesDecodes() throws {
    let m = try decodeGoldenFrame("clip_files.bin")
    let man = try manifest()
    #expect(m.kind == "files")
    let entries = try #require(m.files)
    let names = man["files_names"] as! [String]
    let hashes = man["files_hashes"] as! [String]
    #expect(entries.count == names.count)
    for (i, e) in entries.enumerated() {
        #expect(e.name == names[i])
        let data = try #require(strictBase64Decode(e.content))
        #expect(sha256Hex(data) == hashes[i])          // per-file hash recomputed from bytes
        #expect(e.hash == hashes[i])                    // wire hash matches manifest
        #expect(e.bytes == data.count)
    }
    // Aggregate + total match the Python-canonical manifest values.
    #expect(m.hash == man["files_aggregate"] as? String)
    #expect(aggregateFilesHash(hashes) == man["files_aggregate"] as? String)
    #expect(m.bytes == man["files_total_bytes"] as? Int)
}

@Test func goldenClipFilesTreeDecodes() throws {
    let m = try decodeGoldenFrame("clip_files_path.bin")
    let man = try manifest()
    #expect(m.kind == "files")
    let entries = try #require(m.files)
    let names = man["files_path_names"] as! [String]
    // The canonical vector deliberately MIXES two folder entries with a loose
    // one, so the manifest's path list is [String?] — JSON null for the entry
    // whose frame carries no "path" key at all (verified: its key set is
    // exactly name,content,hash,bytes). That mix is the point: it pins on the
    // wire that a loose entry stays byte-identical to protocol 1.2.
    let paths = (man["files_path_paths"] as! [Any]).map { $0 as? String }
    let hashes = man["files_path_hashes"] as! [String]
    #expect(entries.count == paths.count)
    for (i, e) in entries.enumerated() {
        #expect(e.name == names[i])
        #expect(e.path == paths[i])                       // Python-canonical path
        if let path = e.path {
            #expect(isValidWirePath(path, name: e.name))  // our rules accept Python's
        }
        let data = try #require(strictBase64Decode(e.content))
        #expect(sha256Hex(data) == hashes[i])   // per-file hash recomputed from bytes
        #expect(e.hash == hashes[i])            // wire hash matches manifest
        #expect(e.bytes == data.count)
    }
    // Both shapes must actually be exercised by the vector.
    #expect(entries.contains { $0.path != nil })
    #expect(entries.contains { $0.path == nil })
    // The Python encoder OMITTED "path" entirely for the loose entry — it did
    // not emit null. That omission is exactly what keeps every protocol-1.2
    // frame byte-identical, so assert it on the raw fixture JSON, not on the
    // decoded struct (which cannot tell "absent" from "null" apart).
    let raw = try JSONSerialization.jsonObject(
        with: fixture("clip_files_path.bin").dropFirst(4)) as! [String: Any]
    let rawEntries = raw["files"] as! [[String: Any]]
    let loose = try #require(paths.firstIndex(where: { $0 == nil }))
    #expect(rawEntries[loose].keys.sorted() == ["bytes", "content", "hash", "name"])
    // Aggregate + total match the Python-canonical manifest values: adding
    // "path" must not change how a files clip hashes.
    #expect(m.hash == man["files_path_aggregate"] as? String)
    #expect(aggregateFilesHash(hashes) == man["files_path_aggregate"] as? String)
    #expect(m.bytes == man["files_path_total_bytes"] as? Int)
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
