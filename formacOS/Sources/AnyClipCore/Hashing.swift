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

/// Echo-suppression key for a multi-file clip. Sort the per-file sha256
/// lowercase-hex strings lexicographically (hex is ASCII, so Swift's default
/// String `<` gives the required plain ordinal order), concatenate with no
/// separator, and sha256 the ASCII bytes. Order-independent so pasteboard
/// re-detection order can never break suppression. Keep in lockstep with
/// anyclip.aggregate_files_hash and C# Hashing.AggregateFilesHash.
public func aggregateFilesHash(_ hashes: [String]) -> String {
    sha256Hex(hashes.sorted().joined())
}
