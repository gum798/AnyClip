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

/// Sanitize an inbound file name into a cross-platform-safe basename.
/// Denylist (not whitelist) so legitimate punctuation like "(", "&", ")" and
/// spaces survive. Identical semantics in Python (anyclip.sanitize_filename)
/// and C# (TextHelpers.SanitizeFilename):
///   1. NFC normalize.
///   2. Basename: split on both "/" and "\", keep the last component.
///   3. Replace \ / < > : " | ? *, controls < U+0020, and U+007F with "_".
///   4. Trim trailing dots and spaces.
///   5. Empty / "." / ".." -> "received.bin".
///   6. Windows reserved device names (CON PRN AUX NUL COM1-9 LPT1-9,
///      case-insensitive, matched on the stem before the FIRST dot) -> "_"-prefixed.
public func sanitizeFilename(_ name: String) -> String {
    let nfc = name.precomposedStringWithCanonicalMapping
    // omittingEmptySubsequences: false + .last reproduces Python's
    // rsplit("/", 1)[-1] EXACTLY: a trailing separator yields "" (-> the
    // received.bin fallback below), not the component before it. Keep in
    // lockstep with anyclip.sanitize_filename and C# TextHelpers.SanitizeFilename.
    let base = nfc.split(omittingEmptySubsequences: false,
                         whereSeparator: { $0 == "/" || $0 == "\\" })
        .last.map(String.init) ?? ""
    let deny: Set<Character> = ["\\", "/", "<", ">", ":", "\"", "|", "?", "*"]
    var out = ""
    for scalar in base.unicodeScalars {
        if scalar.value < 0x20 || scalar.value == 0x7F || deny.contains(Character(scalar)) {
            out.append("_")
        } else {
            out.append(Character(scalar))
        }
    }
    while let last = out.last, last == "." || last == " " { out.removeLast() }
    if out.isEmpty || out == "." || out == ".." { return "received.bin" }
    let stem = out.split(separator: ".", maxSplits: 1,
                         omittingEmptySubsequences: false).first.map(String.init) ?? out
    let upper = stem.uppercased()
    let isCom = upper.count == 4 && upper.hasPrefix("COM") && ("1"..."9").contains(upper.last!)
    let isLpt = upper.count == 4 && upper.hasPrefix("LPT") && ("1"..."9").contains(upper.last!)
    if ["CON", "PRN", "AUX", "NUL"].contains(upper) || isCom || isLpt {
        out = "_" + out
    }
    return out
}

/// One name's uniquify step against the names already taken: the first
/// occurrence keeps its name, later duplicates get " (2)", " (3)" … inserted
/// before the LAST extension (a leading dot is not an extension:
/// ".env" -> ".env (2)"). `used` is updated with whatever is returned, so the
/// caller can seed it with names that are already spoken for (the rebuilt
/// folder tops of the same clip).
///
/// `alsoTaken` covers the names the caller cannot enumerate into `used` —
/// what is already sitting in received/ on disk. received/ holds TREES now,
/// so a loose file named like a folder already there must be bumped rather
/// than planned straight onto a directory; anyclip.py expresses the same rule
/// as `uniquify_names(sorted(existing) + names)[len(existing):]` and
/// `uniquify_against(name, used)`. Keep in lockstep with the Python/C#
/// receivers.
public func uniquifyName(
    _ name: String, used: inout Set<String>, alsoTaken: (String) -> Bool = { _ in false }
) -> String {
    func isFree(_ candidate: String) -> Bool {
        !used.contains(candidate) && !alsoTaken(candidate)
    }
    if isFree(name) {
        used.insert(name)
        return name
    }
    let stem: String
    let ext: String
    if let dot = name.lastIndex(of: "."), dot != name.startIndex {
        stem = String(name[..<dot])
        ext = String(name[dot...])
    } else {
        stem = name
        ext = ""
    }
    var n = 2
    var candidate = "\(stem) (\(n))\(ext)"
    while !isFree(candidate) {
        n += 1
        candidate = "\(stem) (\(n))\(ext)"
    }
    used.insert(candidate)
    return candidate
}

/// De-duplicate names WITHIN one received batch, after sanitization.
public func uniquifyNames(_ names: [String]) -> [String] {
    var used = Set<String>()
    return names.map { uniquifyName($0, used: &used) }
}
