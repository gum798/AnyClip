import Testing
import Foundation
@testable import AnyClipCore

private func tempDir() -> URL {
    let url = FileManager.default.temporaryDirectory
        .appendingPathComponent("anyclip-test-\(UUID().uuidString)")
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@Test func loadMissingReturnsNil() {
    #expect(ConfigStore.load(dir: tempDir()) == nil)
}

@Test func saveThenLoadRoundTrips() throws {
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "secret-token"), dir: dir)
    #expect(ConfigStore.load(dir: dir)?.token == "secret-token")
}

@Test func savedFileHas0600Permissions() throws {
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "t"), dir: dir)
    let attrs = try FileManager.default.attributesOfItem(
        atPath: ConfigStore.configPath(dir: dir).path)
    #expect((attrs[.posixPermissions] as? Int) == 0o600)
}

@Test func savedFileIsReadableByPythonShape() throws {
    // Python json.load() must see {"token": "..."}.
    let dir = tempDir()
    try ConfigStore.save(StoredConfig(token: "abc"), dir: dir)
    let raw = try Data(contentsOf: ConfigStore.configPath(dir: dir))
    let obj = try JSONSerialization.jsonObject(with: raw) as? [String: Any]
    #expect(obj?["token"] as? String == "abc")
}

@Test func loadsPythonWrittenFile() throws {
    // Same shape config_store.py writes: indent=2, sort_keys, trailing newline.
    let dir = tempDir()
    let body = "{\n  \"token\": \"from-python\"\n}\n"
    try body.write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir)?.token == "from-python")
}

@Test func corruptFileReturnsNil() throws {
    let dir = tempDir()
    try "not json{{{".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func missingTokenKeyReturnsNil() throws {
    let dir = tempDir()
    try "{\"other\": 1}".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func emptyTokenReturnsNil() throws {
    let dir = tempDir()
    try "{\"token\": \"\"}".write(to: ConfigStore.configPath(dir: dir), atomically: true, encoding: .utf8)
    #expect(ConfigStore.load(dir: dir) == nil)
}

@Test func generatedTokensAreUrlSafeAndUnique() {
    let a = ConfigStore.generateToken()
    let b = ConfigStore.generateToken()
    #expect(a != b)
    #expect(a.count >= 42) // 32 bytes base64url ≈ 43 chars
    let allowed = CharacterSet(charactersIn:
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_")
    #expect(a.unicodeScalars.allSatisfy { allowed.contains($0) })
}
