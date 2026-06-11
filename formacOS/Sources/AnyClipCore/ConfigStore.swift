import Foundation

/// Persistent on-disk store for AnyClip's shared secret token.
/// Port of config_store.py. Shares ~/.anyclip/config.json with the Python
/// implementation — both sides read/write the same {"token": "..."} shape.

public struct StoredConfig: Sendable, Equatable {
    public var token: String
    public init(token: String) { self.token = token }
}

public enum ConfigStore {
    public static func defaultDir() -> URL {
        FileManager.default.homeDirectoryForCurrentUser
            .appendingPathComponent(".anyclip", isDirectory: true)
    }

    public static func configPath(dir: URL? = nil) -> URL {
        (dir ?? defaultDir()).appendingPathComponent("config.json")
    }

    /// 32 bytes of entropy, base64url without padding — same shape as
    /// Python's secrets.token_urlsafe(32).
    public static func generateToken() -> String {
        var bytes = [UInt8](repeating: 0, count: 32)
        for i in bytes.indices { bytes[i] = UInt8.random(in: .min ... .max) }
        return Data(bytes).base64EncodedString()
            .replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_")
            .replacingOccurrences(of: "=", with: "")
    }

    /// Read the config file. nil if missing or unreadable/corrupt —
    /// a damaged file never blocks startup.
    public static func load(dir: URL? = nil) -> StoredConfig? {
        guard let raw = try? Data(contentsOf: configPath(dir: dir)) else { return nil }
        guard let obj = try? JSONSerialization.jsonObject(with: raw) as? [String: Any],
              let token = obj["token"] as? String, !token.isEmpty
        else { return nil }
        return StoredConfig(token: token)
    }

    /// Atomically write the config with 0600 permissions: temp file in the
    /// same directory, chmod, then rename(2).
    public static func save(_ config: StoredConfig, dir: URL? = nil) throws {
        let targetDir = dir ?? defaultDir()
        try FileManager.default.createDirectory(at: targetDir, withIntermediateDirectories: true)
        let target = configPath(dir: targetDir)
        let data = try JSONSerialization.data(
            withJSONObject: ["token": config.token],
            options: [.prettyPrinted, .sortedKeys])
        let tmp = targetDir.appendingPathComponent(".config.json.\(UUID().uuidString).tmp")
        do {
            try (data + Data("\n".utf8)).write(to: tmp)
            let handle = try FileHandle(forWritingTo: tmp)
            try handle.synchronize()   // fsync before rename, like config_store.py
            try handle.close()
            try FileManager.default.setAttributes(
                [.posixPermissions: 0o600], ofItemAtPath: tmp.path)
            guard rename(tmp.path, target.path) == 0 else {
                throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO)
            }
        } catch {
            try? FileManager.default.removeItem(at: tmp)
            throw error
        }
    }
}
