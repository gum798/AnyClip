import Foundation

/// Where ONE received entry lands under received/.
public struct TreePlacement: Equatable, Sendable {
    /// POSIX-relative path under received/ ("<top>/<sub>/<name>", or just the
    /// file name for a loose entry). Every segment is already sanitized.
    public let relativePath: String
    /// First component of relativePath — the item this entry contributes to the
    /// clipboard (a rebuilt folder, or the loose file itself).
    public let top: String
    /// True when the entry landed inside a rebuilt folder.
    public let inTree: Bool
    public init(relativePath: String, top: String, inTree: Bool) {
        self.relativePath = relativePath
        self.top = top
        self.inTree = inTree
    }
}

/// Placement plan for one inbound kind:"files" batch. Pure — no IO: the caller
/// supplies `exists` (is there already a received/<name>?). Keep in lockstep
/// with anyclip.plan_received_layout and C# ReceivedTree.Plan.
public enum ReceivedTree {
    /// Rules:
    ///  - No path, or a path that violates ANY wire rule -> FLAT placement under
    ///    the sanitized name. An entry is never dropped and never escapes
    ///    received/ (validation rejects "..", and sanitizeFilename would map a
    ///    surviving ".." to "received.bin" anyway).
    ///  - A valid single-segment path is a loose file, not a tree.
    ///  - Tops are reserved in first-appearance order and uniquified as
    ///    "<top>-2", "<top>-3" …; every entry sharing a top gets the SAME
    ///    replacement, so one clip lands in one new folder.
    ///  - Loose names then uniquify (" (2)") against those reserved tops AND
    ///    against what is already on disk, so a loose file named like a folder
    ///    sitting in received/ is bumped instead of being planned onto it.
    public static func plan(
        _ entries: [(name: String, data: Data, relPath: String?)],
        exists: (String) -> Bool
    ) -> [TreePlacement] {
        var used = Set<String>()               // reserved top-level names
        var topMap: [String: String] = [:]     // sanitized wire top -> placed top
        var segmentsByIndex: [Int: [String]] = [:]

        // Pass 1: reserve the tree tops, in first-appearance order.
        for (i, e) in entries.enumerated() {
            guard let raw = e.relPath, isValidWirePath(raw, name: e.name) else { continue }
            let segments = pathSegments(sanitizeWirePath(raw))
            guard segments.count >= 2 else { continue }
            segmentsByIndex[i] = segments
            let wireTop = segments[0]
            guard topMap[wireTop] == nil else { continue }
            var candidate = wireTop
            var n = 2
            while used.contains(candidate) || exists(candidate) {
                candidate = "\(wireTop)-\(n)"
                n += 1
            }
            used.insert(candidate)
            topMap[wireTop] = candidate
        }

        // Pass 2: emit placements in batch order.
        var out: [TreePlacement] = []
        for (i, e) in entries.enumerated() {
            if let segments = segmentsByIndex[i], let top = topMap[segments[0]] {
                let rel = ([top] + segments.dropFirst()).joined(separator: "/")
                out.append(TreePlacement(relativePath: rel, top: top, inTree: true))
                continue
            }
            let flat = uniquifyName(
                sanitizeFilename(e.name), used: &used, alsoTaken: exists)
            out.append(TreePlacement(relativePath: flat, top: flat, inTree: false))
        }
        return out
    }

    /// Split a POSIX path on SCALARS, not Characters, for the same reason
    /// isValidWirePath scans separators by scalar: a combining mark right after
    /// "/" forms one grapheme cluster with it, so a Character-level split would
    /// not break where Python's code-point split does. Mirrors
    /// `[sanitize_filename(seg) for seg in rel.split("/")]`.
    public static func pathSegments(_ path: String) -> [String] {
        Array(path.unicodeScalars)
            .split(separator: "/")
            .map { String(String.UnicodeScalarView($0)) }
    }
}
