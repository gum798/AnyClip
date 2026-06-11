import CryptoKit
import Foundation

/// Hex SHA-256 of raw bytes — same as Python's hashlib.sha256(data).hexdigest().
public func sha256Hex(_ data: Data) -> String {
    SHA256.hash(data: data).map { String(format: "%02x", $0) }.joined()
}

/// Hex SHA-256 of the UTF-8 encoding of `text`.
public func sha256Hex(_ text: String) -> String {
    sha256Hex(Data(text.utf8))
}
