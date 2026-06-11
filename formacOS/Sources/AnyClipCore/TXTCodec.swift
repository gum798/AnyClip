import Foundation

/// Minimal DNS TXT record codec (RFC 6763 §6): each entry is one length
/// byte followed by "key=value" bytes. Used to advertise/parse the same
/// properties the Python zeroconf beacon uses. Entries over 255 bytes are
/// skipped (ours are all tiny).
public enum TXTCodec {
    public static func encode(_ entries: [(String, String)]) -> Data {
        var out = Data()
        for (key, value) in entries {
            let raw = Data("\(key)=\(value)".utf8)
            guard raw.count <= 255 else { continue }
            out.append(UInt8(raw.count))
            out.append(raw)
        }
        return out
    }

    public static func decode(_ data: Data) -> [String: String] {
        var result: [String: String] = [:]
        var i = data.startIndex
        while i < data.endIndex {
            let len = Int(data[i])
            i = data.index(after: i)
            // A zero-length entry ends the scan (break, not continue): we only
            // decode records we or zeroconf encoded, where 0x00 means "empty
            // TXT record", never padding between entries.
            guard len > 0,
                  let end = data.index(i, offsetBy: len, limitedBy: data.endIndex)
            else { break }
            if let s = String(data: data[i..<end], encoding: .utf8),
               let eq = s.firstIndex(of: "=") {
                result[String(s[..<eq])] = String(s[s.index(after: eq)...])
            }
            i = end
        }
        return result
    }
}
