import Foundation

/// Protocol constants — keep in lockstep with anyclip.py.
public enum Wire {
    /// 64 MiB hard cap per frame (fits a ~16 MB pptx).
    public static let maxPayload = 64 * 1024 * 1024
    /// The receive cap enforced by peers on protocol minor < 2: they CLOSE the
    /// session on a bigger frame, so the broadcast fan-out gates per link on
    /// this value rather than letting an oversize clip tear an old peer's link
    /// down. See linkAcceptsFrame.
    public static let legacyMaxPayload = 16 * 1024 * 1024
    public static let protocolMajor = 1
    /// Cumulative feature level: minor >= 1 accepts kind:"files", minor >= 2
    /// accepts frames up to maxPayload (64 MiB) instead of legacyMaxPayload,
    /// minor >= 3 rebuilds folder trees from each entry's optional "path".
    /// Minor 3 is a capability MARKER only: it gates nothing on the send path.
    public static let protocolMinor = 3
    /// Wire-path caps for a kind:"files" entry's optional "path" field.
    /// Keep in lockstep with anyclip.MAX_PATH_SEGMENTS / MAX_PATH_CHARS and
    /// C# Wire.MaxPathSegments / Wire.MaxSanitizedPathLength.
    public static let maxPathSegments = 32
    public static let maxPathLength = 240
    /// Legacy single-int field old peers read; equals protocolMajor.
    public static let legacyVersion = 1
    public static let defaultPort: UInt16 = 24816
    public static let serviceType = "_anyclip._tcp"
    public static let handshakeTimeout: Double = 5.0
    public static let connectTimeout: Double = 5.0
    /// BASE upper bound on a single app-initiated send. A send whose completion
    /// is lost (connection cancelled mid-send) or that parks on a full TCP
    /// buffer would otherwise freeze the caller -- the clipboard poll loop and
    /// the heartbeat self-heal both await sends inline. On timeout the
    /// connection is cancelled to force a reconnect. The EFFECTIVE budget
    /// scales with the frame; see sendTimeoutFor.
    public static let sendTimeout: Double = 10.0
    /// Window after link-up in which a duplicate handshake is a connect
    /// race (node_id tie-breaker); later arrivals replace a stale link.
    public static let raceWindow: Double = 1.5
    public static let maxReconnectFails = 3

    /// Drain budget for a frame body of `bytes`: the base timeout plus one
    /// second per MiB (a 1 MiB/s floor). A fixed 10 s could not carry a 64 MiB
    /// frame over a slow LAN, and a timeout closes the connection.
    ///
    /// Invariant: worst case 64 MiB -> 10 + 64 = 74 s, which stays below the
    /// 90 s per-link staleness deadline (linkPingLoop: 30 s ping x dead factor
    /// 3), so a legitimately slow big send can never be mistaken for a
    /// half-open link. Keep in lockstep with anyclip.send_timeout_for.
    public static func sendTimeoutFor(bytes: Int, base: Double = sendTimeout) -> Double {
        base + Double(bytes) / (1024 * 1024)
    }

    /// False when a frame body of `bytes` would breach the legacy 16 MiB
    /// receive cap that a peer on protocol minor < 2 still enforces. Such a
    /// peer closes the session on an over-cap frame, so the fan-out skips the
    /// send and KEEPS the link instead.
    public static func linkAcceptsFrame(bytes: Int, peerMinor: Int) -> Bool {
        bytes <= legacyMaxPayload || peerMinor >= 2
    }

    /// Receive-side frame-length guard: a body of 1...maxPayload bytes. Peers
    /// on protocol minor < 2 apply this same rule against legacyMaxPayload,
    /// which is exactly what linkAcceptsFrame protects them from.
    public static func acceptsFrameLength(_ n: Int) -> Bool {
        n > 0 && n <= maxPayload
    }
}

public enum WireFrameError: Error, Equatable {
    case payloadTooLarge(Int)
}

/// One encoded wire frame: the bytes to write, plus the BODY length separately.
/// Every cap in the protocol (Wire.maxPayload on receive, Wire.legacyMaxPayload
/// in the per-link send gate) is expressed on the body, not on the 4-byte
/// length prefix, so carrying `bodyCount` alongside keeps the boundary exact
/// and lets the mesh fan-out encode a payload variant ONCE and reuse the same
/// bytes for both the size gate and every send of that variant.
public struct EncodedFrame: Sendable, Equatable {
    public let bytes: Data
    public let bodyCount: Int
    public init(bytes: Data, bodyCount: Int) {
        self.bytes = bytes
        self.bodyCount = bodyCount
    }
}

/// A semantic clipboard payload, decoupled from its wire encoding.
public enum ClipPayload: Sendable {
    case text(String)
    case image(Data)
    case file(name: String, data: Data)
    case files([(name: String, data: Data, relPath: String?)])

    public var kind: String {
        switch self {
        case .text: return "text"
        case .image: return "image"
        case .file: return "file"
        case .files: return "files"
        }
    }

    public var payloadHash: String {
        switch self {
        case .text(let s): return sha256Hex(s)
        case .image(let d): return sha256Hex(d)
        case .file(_, let d): return sha256Hex(d)
        case .files(let fs): return aggregateFilesHash(fs.map { sha256Hex($0.data) })
        }
    }
}

/// One entry inside a kind:"files" clip. Fields name,content,hash,bytes are the
/// canonical wire keys (the order Python/C# emit). Swift's Foundation
/// JSONEncoder does not guarantee key order, but that is fine: JSON objects are
/// unordered and every peer parses by key, so interop never depends on it.
public struct WireFileEntry: Codable, Sendable, Equatable {
    public var name: String
    public var content: String
    public var hash: String
    public var bytes: Int
    /// Relative path INCLUDING the top folder name ("<top>/<sub>/<name>"),
    /// present only for files that came from a copied folder. Optional, so
    /// the synthesized encoder omits it (encodeIfPresent) and a loose file's
    /// entry is byte-identical to protocol 1.2. Peers below minor 3 ignore it.
    public var path: String?
    public init(name: String, content: String, hash: String, bytes: Int,
                path: String? = nil) {
        self.name = name
        self.content = content
        self.hash = hash
        self.bytes = bytes
        self.path = path
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
    public var files: [WireFileEntry]?
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

    public static func clipFiles(
        files: [(name: String, data: Data, relPath: String?)], ts: Double
    ) -> WireMessage {
        var m = WireMessage(type: "clip")
        m.kind = "files"
        var entries: [WireFileEntry] = []
        var hashes: [String] = []
        var total = 0
        for f in files {
            let h = sha256Hex(f.data)
            let name = f.name.precomposedStringWithCanonicalMapping  // NFC on the wire
            var path: String?
            if let rel = f.relPath {
                let nfc = rel.precomposedStringWithCanonicalMapping  // NFC on the wire
                if isValidWirePath(nfc, name: name) {
                    path = nfc
                } else {
                    AnyLog.shared.warning(
                        "invalid wire path '\(rel)' for \(name); sending it flat")
                }
            }
            entries.append(WireFileEntry(
                name: name, content: f.data.base64EncodedString(),
                hash: h, bytes: f.data.count, path: path))
            hashes.append(h)
            total += f.data.count
        }
        m.files = entries
        m.hash = aggregateFilesHash(hashes)  // top-level hash = aggregate
        m.ts = ts
        m.bytes = total                       // sum of raw byte counts
        return m
    }

    public static func clip(_ payload: ClipPayload, ts: Double) -> WireMessage {
        switch payload {
        case .text(let s): return clipText(s, ts: ts)
        case .image(let d): return clipImage(d, ts: ts)
        case .file(let n, let d): return clipFile(name: n, data: d, ts: ts)
        case .files(let fs): return clipFiles(files: fs, ts: ts)
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
    /// 4-byte big-endian length prefix + UTF-8 JSON body, with the body length
    /// carried alongside for the size gates (see EncodedFrame).
    public func encode() throws -> EncodedFrame {
        let encoder = JSONEncoder()
        // ensure_ascii=False equivalent: JSONEncoder already outputs raw
        // Unicode by default (non-ASCII chars pass through unescaped).
        let body = try encoder.encode(self)
        guard body.count <= Wire.maxPayload else {
            throw WireFrameError.payloadTooLarge(body.count)
        }
        var out = Data(capacity: 4 + body.count)
        let n = UInt32(body.count) // safe: guarded to maxPayload (64 MiB) above, far below UInt32.max
        out.append(UInt8((n >> 24) & 0xFF))
        out.append(UInt8((n >> 16) & 0xFF))
        out.append(UInt8((n >> 8) & 0xFF))
        out.append(UInt8(n & 0xFF))
        out.append(body)
        return EncodedFrame(bytes: out, bodyCount: body.count)
    }

    /// Frame bytes only, for callers that do not need the body length.
    public func encodeFrame() throws -> Data { try encode().bytes }

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

/// Decode a kind:"files" message's entries into (name, rawBytes, relPath).
/// Returns nil if the array is empty/nil OR ANY entry has non-strict base64
/// content — the caller drops the WHOLE frame (no partial apply). Names AND
/// paths pass through raw; sanitize/validate/uniquify happen write-side, so a
/// bad path degrades that ONE entry to flat placement instead of killing the
/// frame. Hashes are never trusted from the wire.
public func decodeFileEntries(
    _ files: [WireFileEntry]?
) -> [(name: String, data: Data, relPath: String?)]? {
    guard let files, !files.isEmpty else { return nil }
    var out: [(name: String, data: Data, relPath: String?)] = []
    for e in files {
        guard let data = strictBase64Decode(e.content) else { return nil }
        out.append((name: e.name, data: data, relPath: e.path))
    }
    return out
}

/// Sanitized POSIX form of a wire path: every segment through sanitizeFilename
/// (NFC + denylist + trailing dot/space trim + Windows reserved names),
/// rejoined with "/". Used for the length rule below and by the receiver when
/// it rebuilds the tree, so both judge the same string.
public func sanitizeWirePath(_ path: String) -> String {
    // Split on SCALARS, not Characters: a combining mark directly after "/"
    // forms one grapheme cluster with it, so a Character-level split would not
    // break where Python's code-point split does. Keep in lockstep with
    // anyclip.sanitize_relpath.
    wirePathSegments(path)
        .map(sanitizeFilename)
        .joined(separator: "/")
}

/// The "/"-separated segments of a wire path, split on unicode scalars so the
/// segmentation is identical to Python's `path.split("/")`. Empty segments are
/// preserved so the caller can reject them.
private func wirePathSegments(_ path: String) -> [String] {
    Array(path.unicodeScalars)
        .split(separator: "/", omittingEmptySubsequences: false)
        .map { slice in
            var s = ""
            s.unicodeScalars.append(contentsOf: slice)
            return s
        }
}

/// True when `path` satisfies EVERY wire rule for a folder entry's optional
/// "path": POSIX "/" separators, NFC, relative (no leading "/", no drive
/// letter), no "." / ".." / empty segments, no backslashes, last segment equals
/// `name`, <= Wire.maxPathSegments segments, sanitized length <=
/// Wire.maxPathLength. Senders MUST only emit paths that pass; receivers MUST
/// verify before rebuilding a tree and fall back to FLAT placement for that ONE
/// entry when they do not.
///
/// NFC IS A REJECTION RULE, not a normalization: a decomposed path is refused
/// outright and the entry lands flat, exactly as anyclip.is_valid_wire_path
/// does (`if path != unicodedata.normalize("NFC", path): return False`, pinned
/// by tests/test_wire_files.py::test_only_nfc_paths_are_accepted_on_the_wire).
/// The comparison MUST be byte-level: Swift's String == is canonical, so
/// `path == NFC(path)` is true for a decomposed path and cannot express this
/// check at all. C# (Task 7) must compare ordinally for the same reason — and
/// because its String.Normalize() throws on invalid UTF-16, a byte/ordinal
/// comparison is also the safe construction there.
///
/// SEPARATOR SCANS COUNT SCALARS, not Characters: a combining mark right after
/// "/" or "\" forms a single grapheme cluster with it, so a Character-level
/// scan would miss the separator that Python's code-point scan finds and the
/// two would disagree about where segments begin.
///
/// LENGTH IS COUNTED IN UNICODE SCALARS (code points), matching Python's
/// len(). String.count would count grapheme clusters and C# string.Length
/// UTF-16 units, so those three disagree on any emoji/non-BMP path and the
/// same clip would rebuild a tree on one receiver and flat-place on another;
/// C# must count runes, not .Length. Keep in lockstep with
/// anyclip.is_valid_wire_path and C# Wire.IsValidRelPath.
public func isValidWirePath(_ path: String, name: String) -> Bool {
    guard !path.isEmpty else { return false }
    // Byte-level, because String == is canonical and would call NFD equal.
    guard Array(path.utf8)
            == Array(path.precomposedStringWithCanonicalMapping.utf8) else { return false }
    let scalars = Array(path.unicodeScalars)
    guard !scalars.contains("\\") else { return false }
    let segments = wirePathSegments(path)
    guard !segments.isEmpty, segments.count <= Wire.maxPathSegments else { return false }
    for segment in segments where segment.isEmpty || segment == "." || segment == ".." {
        return false
    }
    let firstChar = Character(scalars[0])
    if scalars.count > 1, scalars[1] == ":", firstChar.isASCII, firstChar.isLetter {
        return false   // drive letter ("C:/...")
    }
    // Byte-level again: Python compares segments[-1] != name exactly, so a
    // composed path with a decomposed `name` must NOT match here either.
    guard Array(segments[segments.count - 1].utf8) == Array(name.utf8) else { return false }
    return sanitizeWirePath(path).unicodeScalars.count <= Wire.maxPathLength
}
