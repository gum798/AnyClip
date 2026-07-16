import Foundation

/// Protocol constants — keep in lockstep with anyclip.py.
public enum Wire {
    public static let maxPayload = 16 * 1024 * 1024
    public static let protocolMajor = 1
    public static let protocolMinor = 0
    /// Legacy single-int field old peers read; equals protocolMajor.
    public static let legacyVersion = 1
    public static let defaultPort: UInt16 = 24816
    public static let serviceType = "_anyclip._tcp"
    public static let handshakeTimeout: Double = 5.0
    public static let connectTimeout: Double = 5.0
    /// Upper bound on a single app-initiated send. A send whose completion is
    /// lost (connection cancelled mid-send) or that parks on a full TCP buffer
    /// would otherwise freeze the caller -- the clipboard poll loop and the
    /// heartbeat self-heal both await sends inline. On timeout the connection
    /// is cancelled to force a reconnect.
    public static let sendTimeout: Double = 10.0
    /// Window after link-up in which a duplicate handshake is a connect
    /// race (node_id tie-breaker); later arrivals replace a stale link.
    public static let raceWindow: Double = 1.5
    public static let maxReconnectFails = 3
}

public enum WireFrameError: Error, Equatable {
    case payloadTooLarge(Int)
}

/// A semantic clipboard payload, decoupled from its wire encoding.
public enum ClipPayload: Sendable {
    case text(String)
    case image(Data)
    case file(name: String, data: Data)

    public var kind: String {
        switch self {
        case .text: return "text"
        case .image: return "image"
        case .file: return "file"
        }
    }

    public var payloadHash: String {
        switch self {
        case .text(let s): return sha256Hex(s)
        case .image(let d): return sha256Hex(d)
        case .file(_, let d): return sha256Hex(d)
        }
    }
}

/// One wire message. A single optional-field struct covers every frame type
/// (hello/clip/ping/pong) — JSONEncoder omits nil fields, JSONDecoder
/// tolerates extras, which matches the Python dict-based protocol.
/// snake_case property names ARE the wire field names; do not rename.
public struct WireMessage: Codable, Sendable, Equatable {
    public var type: String
    public var token: String?
    public var node_id: String?
    public var name: String?
    public var version: Int?
    public var app_version: String?
    public var protocol_major: Int?
    public var protocol_minor: Int?
    public var kind: String?
    public var content: String?
    public var hash: String?
    public var ts: Double?
    public var bytes: Int?

    public init(type: String) { self.type = type }
}

extension WireMessage {
    public static func hello(
        tokenHash: String, nodeID: String, name: String, appVersion: String
    ) -> WireMessage {
        var m = WireMessage(type: "hello")
        m.token = tokenHash
        m.node_id = nodeID
        m.name = name
        m.version = Wire.legacyVersion
        m.app_version = appVersion
        m.protocol_major = Wire.protocolMajor
        m.protocol_minor = Wire.protocolMinor
        return m
    }

    public static func clipText(_ text: String, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "text"
        m.content = text
        m.hash = sha256Hex(text)
        m.ts = ts
        return m
    }

    public static func clipImage(_ png: Data, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "image"
        m.content = png.base64EncodedString()
        m.hash = sha256Hex(png)
        m.ts = ts
        m.bytes = png.count
        return m
    }

    public static func clipFile(name: String, data: Data, ts: Double) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "file"
        // NFC on the wire: macOS reads filenames in NFD (decomposed Hangul =
        // conjoining jamo U+11xx a Windows peer can't render). Normalize so
        // every receiver gets a composed, renderable name. Keep in lockstep
        // with anyclip.send_clip and C# WireMessage.ClipFile.
        m.name = name.precomposedStringWithCanonicalMapping
        m.content = data.base64EncodedString()
        m.hash = sha256Hex(data)
        m.ts = ts
        m.bytes = data.count
        return m
    }

    public static func clip(_ payload: ClipPayload, ts: Double) -> WireMessage {
        switch payload {
        case .text(let s): return clipText(s, ts: ts)
        case .image(let d): return clipImage(d, ts: ts)
        case .file(let n, let d): return clipFile(name: n, data: d, ts: ts)
        }
    }

    public static func ping(ts: Double) -> WireMessage {
        var m = WireMessage(type: "ping")
        m.ts = ts
        return m
    }

    public static func pong(ts: Double) -> WireMessage {
        var m = WireMessage(type: "pong")
        m.ts = ts
        return m
    }
}

extension WireMessage {
    /// 4-byte big-endian length prefix + UTF-8 JSON body.
    public func encodeFrame() throws -> Data {
        let encoder = JSONEncoder()
        // ensure_ascii=False equivalent: JSONEncoder already outputs raw
        // Unicode by default (non-ASCII chars pass through unescaped).
        let body = try encoder.encode(self)
        guard body.count <= Wire.maxPayload else {
            throw WireFrameError.payloadTooLarge(body.count)
        }
        var out = Data(capacity: 4 + body.count)
        let n = UInt32(body.count) // safe: guarded to maxPayload (16 MiB) above, far below UInt32.max
        out.append(UInt8((n >> 24) & 0xFF))
        out.append(UInt8((n >> 16) & 0xFF))
        out.append(UInt8((n >> 8) & 0xFF))
        out.append(UInt8(n & 0xFF))
        out.append(body)
        return out
    }

    /// Big-endian length from the 4-byte header. Alignment-safe for slices.
    public static func frameLength(_ header: Data) -> Int {
        guard header.count >= 4 else { return 0 } // 0 is already an invalid frame length
        var n = 0
        for byte in header.prefix(4) { n = (n << 8) | Int(byte) }
        return n
    }

    /// nil on malformed JSON — caller treats that as end-of-session.
    public static func decodeBody(_ body: Data) -> WireMessage? {
        let decoder = JSONDecoder()
        return try? decoder.decode(WireMessage.self, from: body)
    }

    /// Peer version with backward-compat defaults: an old peer only sends
    /// `version`, treated as protocol_major with minor 0 / unknown app.
    public func peerVersionInfo() -> VersionInfo {
        let major = protocol_major ?? version ?? 0
        let app = (app_version?.isEmpty == false) ? app_version! : "unknown"
        return VersionInfo(appVersion: app, protocolMajor: major,
                           protocolMinor: protocol_minor ?? 0)
    }
}

/// Strict base64 decode — Data(base64Encoded:) rejects invalid input,
/// mirroring Python's b64decode(validate=True).
public func strictBase64Decode(_ s: String) -> Data? {
    Data(base64Encoded: s)
}
