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
    ///  - LOOSE names are uniquified FIRST (" (2)"), against each other and
    ///    against what is already on disk, so a loose file keeps the name the
    ///    sender gave it.
    ///  - Tree tops are reserved AFTER that, in first-appearance order, against
    ///    those loose names and the disk, and uniquified as "<top>-2",
    ///    "<top>-3" …; every entry sharing a top gets the SAME replacement, so
    ///    one clip lands in one new folder.
    ///
    /// The order matters and is the canonical one: anyclip.plan_received_layout
    /// computes `uniquify_names(sorted(existing) + loose)[len(existing):]` and
    /// only then walks the tops against `used = existing | set(loose)`. A clip
    /// of [docs/a.txt, loose "docs"] therefore lands as
    /// ["docs-2/a.txt", "docs"] — the TOP moves, never the loose file.
    public static func plan(
        _ entries: [(name: String, data: Data, relPath: String?)],
        exists: (String) -> Bool
    ) -> [TreePlacement] {
        var used = Set<String>()               // top-level names already spoken for
        var flatByIndex: [Int: String] = [:]   // batch index -> placed loose name
        var segmentsByIndex: [Int: [String]] = [:]

        // Pass 1: the loose names, in batch order. Anything without a path,
        // with a path that violates a wire rule, or with a single-segment path
        // is loose. `alsoTaken` is the on-disk half of Python's `existing`.
        for (i, e) in entries.enumerated() {
            if let raw = e.relPath, isValidWirePath(raw, name: e.name) {
                let segments = pathSegments(sanitizeWirePath(raw))
                if segments.count >= 2 {
                    segmentsByIndex[i] = segments
                    continue
                }
            }
            flatByIndex[i] = uniquifyName(
                sanitizeFilename(e.name), used: &used, alsoTaken: exists)
        }

        // Pass 2: reserve the tree tops against the loose names AND the disk.
        var topMap: [String: String] = [:]     // sanitized wire top -> placed top
        for i in entries.indices {
            guard let segments = segmentsByIndex[i] else { continue }
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

        // Pass 3: emit placements in batch order.
        return entries.indices.map { i in
            if let segments = segmentsByIndex[i], let top = topMap[segments[0]] {
                let rel = ([top] + segments.dropFirst()).joined(separator: "/")
                return TreePlacement(relativePath: rel, top: top, inTree: true)
            }
            let flat = flatByIndex[i] ?? sanitizeFilename(entries[i].name)
            return TreePlacement(relativePath: flat, top: flat, inTree: false)
        }
    }

    /// Split a POSIX path on SCALARS, not Characters, for the same reason
    /// isValidWirePath scans separators by scalar: a combining mark right after
    /// "/" forms one grapheme cluster with it, so a Character-level split would
    /// not break where Python's code-point split does. Empty segments are
    /// PRESERVED, exactly like Python's `str.split("/")` and the private
    /// `wirePathSegments` in WireProtocol.swift, so this never silently repairs
    /// a path the validator is supposed to reject. Mirrors
    /// `[sanitize_filename(seg) for seg in rel.split("/")]`.
    public static func pathSegments(_ path: String) -> [String] {
        Array(path.unicodeScalars)
            .split(separator: "/", omittingEmptySubsequences: false)
            .map { String(String.UnicodeScalarView($0)) }
    }
}
