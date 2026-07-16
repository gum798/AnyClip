import Foundation

/// One-line preview suitable for a toast body. Port of anyclip.preview().
public func preview(_ text: String, maxLen: Int = 80) -> String {
    let snippet = text
        .replacingOccurrences(of: "\r", with: " ")
        .replacingOccurrences(of: "\n", with: " ")
        .trimmingCharacters(in: .whitespaces)
    if snippet.isEmpty { return "(empty)" }
    if snippet.count <= maxLen { return snippet }
    return String(snippet.prefix(maxLen)) + "..."
}

/// Sanitize an inbound file name: basename only, then replace anything
/// outside [unicode-alnum . _ - space] with "_". Port of
/// ClipboardWatcher.update_local_file's sanitizer.
///
/// Note: NSString.lastPathComponent on macOS does NOT treat ":" as a path
/// separator (that was a classic Mac HFS behaviour not carried into
/// Foundation). "/" is still the only separator recognised, so
/// "a/b/c.txt" → "c.txt" works correctly.
public func sanitizeFilename(_ name: String) -> String {
    // Normalize to NFC first: macOS hands filenames to peers in NFD
    // (decomposed Hangul = conjoining jamo U+11xx that Windows can't render).
    // NFC is the cross-platform interchange form. Keep in lockstep with
    // anyclip.update_local_file and C# TextHelpers.SanitizeFilename.
    let base = (name.precomposedStringWithCanonicalMapping as NSString)
        .lastPathComponent
        .trimmingCharacters(in: .whitespaces)
    guard !base.isEmpty else { return "received.bin" }
    let allowed = CharacterSet.alphanumerics
        .union(CharacterSet(charactersIn: "._- "))
    var out = ""
    for scalar in base.unicodeScalars {
        out.append(allowed.contains(scalar) ? Character(scalar) : "_")
    }
    return out
}
