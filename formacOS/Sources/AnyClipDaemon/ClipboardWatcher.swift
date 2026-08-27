import Foundation
import AppKit
import AnyClipCore

/// (path, size, mtime ns, isDirectory) fingerprint so file bytes are only read
/// when the clipboard's file reference actually changes.
/// Port of the Python (path, st_size, st_mtime_ns) tuple.
public struct FileFingerprint: Equatable, Sendable {
    public let path: String
    public let size: Int
    public let mtimeNs: Int64
    public let isDirectory: Bool

    public init?(url: URL) {
        var st = stat()
        guard stat(url.path, &st) == 0 else { return nil }
        path = url.path
        size = Int(st.st_size)
        mtimeNs = Int64(st.st_mtimespec.tv_sec) * 1_000_000_000
            + Int64(st.st_mtimespec.tv_nsec)
        isDirectory = (st.st_mode & S_IFMT) == S_IFDIR
    }
}

/// What one inbound batch put on disk and on the clipboard.
public struct PlacedFiles: Sendable {
    /// Every file written, in batch order; `name` is the path RELATIVE to
    /// received/ ("<top>/<sub>/<leaf>" for tree entries).
    public var files: [(name: String, data: Data)] = []
    /// The items placed on the pasteboard, in batch order: each rebuilt folder
    /// once, plus every loose file.
    public var topLevelItems: [String] = []
    /// The subset of topLevelItems that are rebuilt folders.
    public var folderTops: [String] = []
    public init() {}
}

/// A destination that would leave received/. Never fatal: the caller demotes
/// THAT entry to a flat name and writes it anyway.
struct ReceivedPathEscape: Error, CustomStringConvertible {
    let path: String
    var description: String { "resolved destination escapes received/: \(path)" }
}

/// One item of a scanned selection, in selection order: a loose file
/// (`entries == nil`) or a folder carrying its expansion. Produced by
/// `ClipboardWatcher.scan` so no caller ever walks the same tree twice.
/// Port of the `items` half of anyclip.scan_selection.
public struct ScannedItem: Sendable {
    public let url: URL
    public let size: Int
    public let entries: [FolderFile]?
}

/// Polls NSPasteboard for text/image/file changes and applies inbound
/// updates without echoing them back. Port of anyclip.ClipboardWatcher.
/// changeCount gating means unchanged clipboards cost one property read
/// per poll (cheaper than the Python re-read every cycle).
@MainActor
public final class ClipboardWatcher {

    public struct Callbacks {
        public var onChange: (ClipPayload) async -> Void
        public var onFileSkipped: ((String) async -> Void)?

        public init(
            onChange: @escaping (ClipPayload) async -> Void,
            onFileSkipped: ((String) async -> Void)? = nil
        ) {
            self.onChange = onChange
            self.onFileSkipped = onFileSkipped
        }
    }

    static let imageCooldown: Double = 1.0
    /// Greedy send budget, applied to the SUM of raw file sizes in one clip.
    /// Reserves ~256 KB for the JSON envelope and the b64 1.34× inflation.
    /// Formula unchanged since the 16 MiB days; against the 64 MiB cap it lands
    /// at 49,466,572 (was ~12,221,153).
    /// Mirrors Python: FILE_BUDGET = int((MAX_PAYLOAD - 256*1024) * 0.74)
    /// `nonisolated` so FolderExpander (which is not on the main actor) can
    /// enforce the same caps during its walk.
    nonisolated static let fileBudget = Int(Double(Wire.maxPayload - 256 * 1024) * 0.74)
    /// Sender-side cap; the receiver stays lenient. Matches MAX_FILES_PER_CLIP.
    /// 100 -> 500 for folder sync (protocol 1.3): a document tree passes 100
    /// trivially and fileBudget is the real limit — worst-case extra JSON
    /// envelope still fits the 256 KB reservation baked into fileBudget.
    nonisolated static let maxFilesPerClip = 500

    private let pasteboard: NSPasteboard
    private let pollInterval: Double
    private let callbacks: Callbacks
    private let receivedDir: URL

    // Baselines — seeded in init so whatever sits on the clipboard at
    // startup never fires a spurious initial send.
    private var lastChangeCount: Int
    private var lastText: String?
    private var lastImageHash: String?
    private var lastImageSendAt: Double = 0
    private var lastFileFingerprints: [FileFingerprint] = []

    public init(
        pasteboard: NSPasteboard = .general,
        pollInterval: Double,
        receivedDir: URL,
        callbacks: Callbacks
    ) {
        self.pasteboard = pasteboard
        self.pollInterval = pollInterval
        self.receivedDir = receivedDir
        self.callbacks = callbacks

        // Seed baselines.
        lastChangeCount = pasteboard.changeCount
        lastText = pasteboard.string(forType: .string)
        if let png = Self.grabImage(pasteboard) {
            lastImageHash = sha256Hex(png)
        }
        lastFileFingerprints = Self.fingerprints(for: Self.grabFileURLs(pasteboard))
    }

    // MARK: - Run loop

    public func run() async throws {
        while true {
            await pollOnce()
            try await Task.sleep(nanoseconds: UInt64(pollInterval * 1_000_000_000))
        }
    }

    /// Test seam: one poll cycle without the sleep.
    public func pollOnceForTesting() async { await pollOnce() }

    private func pollOnce() async {
        let count = pasteboard.changeCount
        guard count != lastChangeCount else { return }
        lastChangeCount = count

        // ---- Text --------------------------------------------------------
        // Empty strings update the baseline but are NOT propagated —
        // macOS Screenshot briefly clears the clipboard mid-capture.
        let text = pasteboard.string(forType: .string)
        if let text, text != lastText {
            lastText = text
            if !text.isEmpty {
                await callbacks.onChange(.text(text))
            } else {
                AnyLog.shared.debug("clipboard cleared (empty text); not propagating")
            }
        }

        // ---- Image -------------------------------------------------------
        // Multi-representation floods right after a screenshot are
        // collapsed by the 1-second cooldown.
        if let png = Self.grabImage(pasteboard) {
            let hash = sha256Hex(png)
            if hash != lastImageHash {
                let now = monotonicNow()
                if now - lastImageSendAt < Self.imageCooldown {
                    // Absorb the hash change but do NOT send.
                    lastImageHash = hash
                    AnyLog.shared.debug("image change within cooldown, dropping")
                } else {
                    lastImageHash = hash
                    lastImageSendAt = now
                    await callbacks.onChange(.image(png))
                }
            }
        }

        // ---- File --------------------------------------------------------
        await checkFileClipboard()
    }

    // MARK: - File clipboard

    private func checkFileClipboard() async {
        let urls = Self.grabFileURLs(pasteboard)
        guard !urls.isEmpty else { return }
        // ONE stat + walk pass per poll, OFF the main actor. The walk does run
        // on every poll — noticing an edit deep inside a tree requires that —
        // but FolderExpander bails out at the absolute caps, so its cost is
        // bounded, and it never blocks the UI. What the comparison below saves
        // is the re-SEND and the re-READ of an unchanged selection, not the
        // walk. Mirrors asyncio.to_thread(scan_selection, paths).
        let scanned = await Self.offMainActor { Self.scan(urls) }
        guard scanned.fingerprints != lastFileFingerprints else { return }
        // Record FIRST so the same selection is never re-detected (no retry loop),
        // regardless of what we end up sending.
        lastFileFingerprints = scanned.fingerprints

        var sendable: [(name: String, data: Data, relPath: String?)] = []
        var running = 0
        var skippedForSize = 0
        // Folders that did not fit are named one toast each (the pinned string
        // carries the folder name); empty folders share ONE generic toast.
        var oversizeFolders: [String] = []
        var emptyFolders = 0
        for item in scanned.items {
            if let entries = item.entries {
                let top = item.url.lastPathComponent.precomposedStringWithCanonicalMapping
                if entries.isEmpty {
                    AnyLog.shared.info("folder \(top) has nothing syncable; skipping")
                    emptyFolders += 1
                    continue
                }
                // Per-folder ALL-OR-NOTHING against what the selection has left:
                // total the sizes BEFORE reading a single byte. No partial trees.
                let total = entries.reduce(0) { $0 + $1.size }
                guard sendable.count + entries.count <= Self.maxFilesPerClip,
                      running + total <= Self.fileBudget
                else {
                    AnyLog.shared.info(
                        "folder \(top) skipped: \(entries.count) file(s) / \(total) bytes "
                        + "do not fit the remaining budget")
                    oversizeFolders.append(top)
                    continue
                }
                running += total
                for entry in entries {
                    let url = entry.url
                    guard let data = await Self.offMainActor({ try? Data(contentsOf: url) })
                    else {
                        // A read failure is per-file, exactly like a loose file:
                        // the rest of the tree still goes.
                        AnyLog.shared.warning("file read failed for \(url.path); skipping")
                        continue
                    }
                    sendable.append((
                        name: url.lastPathComponent.precomposedStringWithCanonicalMapping,
                        data: data, relPath: entry.relPath))
                }
                continue
            }
            // Loose files keep today's greedy per-file behaviour.
            if sendable.count >= Self.maxFilesPerClip || running + item.size > Self.fileBudget {
                skippedForSize += 1
                continue
            }
            let url = item.url
            guard let data = await Self.offMainActor({ try? Data(contentsOf: url) }) else {
                AnyLog.shared.warning("file read failed for \(url.path); skipping")
                continue
            }
            running += item.size
            sendable.append((name: url.lastPathComponent, data: data, relPath: nil))
        }
        if let onSkipped = callbacks.onFileSkipped {
            for name in oversizeFolders {
                await onSkipped("folder too large to sync: \(name)")
            }
            if emptyFolders > 0 {
                await onSkipped("folder is empty; nothing to sync")
            }
            if skippedForSize > 0 {
                await onSkipped("\(skippedForSize) file(s) skipped (too large to sync)")
            }
        }
        // 0 sendable -> nothing. Exactly 1 LOOSE file -> legacy .file. Anything
        // else (>= 2 files, or a single file that must carry its path) -> .files.
        if sendable.count == 1, sendable[0].relPath == nil {
            await callbacks.onChange(.file(name: sendable[0].name, data: sendable[0].data))
        } else if !sendable.isEmpty {
            await callbacks.onChange(.files(sendable))
        }
    }

    // MARK: - Inbound (peer → local clipboard)

    public func updateLocalText(_ text: String) {
        // Baseline before write so a racing poll cannot echo.
        lastText = text
        pasteboard.clearContents()
        pasteboard.setString(text, forType: .string)
        lastChangeCount = pasteboard.changeCount
    }

    public func updateLocalImage(_ png: Data) -> Bool {
        lastImageHash = sha256Hex(png)
        pasteboard.clearContents()
        let ok = pasteboard.setData(png, forType: .png)
        lastChangeCount = pasteboard.changeCount
        if !ok { AnyLog.shared.warning("clipboard write (image) failed") }
        return ok
    }

    @discardableResult
    public func updateLocalFile(name: String, data: Data) async -> Bool {
        !(await updateLocalFiles([(name: name, data: data, relPath: nil)])).files.isEmpty
    }

    /// Rebuild one inbound batch under receivedDir — folder entries into their
    /// tree, everything else flat — then place the TOP-LEVEL items (each folder
    /// once, plus every loose file) on the clipboard in ONE writeObjects, in
    /// batch order. Returns what actually landed so the caller can baseline echo
    /// suppression and word the toast.
    @discardableResult
    public func updateLocalFiles(
        _ files: [(name: String, data: Data, relPath: String?)]
    ) async -> PlacedFiles {
        let dir = receivedDir
        // Every byte of disk work — up to 500 files, ~49 MB of writes, and the
        // walk that fingerprints them — runs OFF the main actor; only the
        // pasteboard hand-off below needs to be here. Mirrors Python's
        // `await asyncio.to_thread(watcher.update_local_files, ...)`.
        let written = await Self.offMainActor { Self.writeInbound(files, into: dir) }
        guard !written.tops.isEmpty else { return PlacedFiles() }
        // Baseline the fingerprints (tree files included) to the placed items
        // BEFORE the clipboard write so a racing poll cannot echo. No suspension
        // between the two, so a poll can never observe one without the other.
        lastFileFingerprints = written.fingerprints
        pasteboard.clearContents()
        let ok = pasteboard.writeObjects(written.tops.map { $0 as NSURL })
        lastChangeCount = pasteboard.changeCount
        if !ok { AnyLog.shared.warning("clipboard write (files) failed") }
        return written.placed
    }

    /// Everything one inbound batch does to the filesystem: plan the layout,
    /// rebuild it under `dir`, and fingerprint what is about to be placed.
    /// Pure IO, so `nonisolated` — the caller runs it off the main actor.
    /// Port of the disk half of anyclip.update_local_files.
    nonisolated static func writeInbound(
        _ files: [(name: String, data: Data, relPath: String?)], into dir: URL
    ) -> (placed: PlacedFiles, tops: [URL], fingerprints: [FileFingerprint]) {
        let fm = FileManager.default
        let empty: (PlacedFiles, [URL], [FileFingerprint]) = (PlacedFiles(), [], [])
        do {
            try fm.createDirectory(at: dir, withIntermediateDirectories: true)
        } catch {
            AnyLog.shared.warning("received dir create failed: \(error)")
            return empty
        }
        let root = dir.standardizedFileURL
        // What received/ already holds. Names, not stats: received/ holds TREES
        // now, so "taken" covers directories too — a colliding tree top is
        // bumped to "<top>-2" and a loose file that would land ON one of them
        // gets " (2)". Mirrors anyclip's `existing = {p.name for p in ...}`.
        let existing = Set((try? fm.contentsOfDirectory(atPath: root.path)) ?? [])
        let plan = ReceivedTree.plan(files) { name in
            existing.contains(name)
                || fm.fileExists(atPath: root.appendingPathComponent(name).path)
        }
        // Every name already spoken for, so a demoted entry (below) can clobber
        // neither a planned one nor another demotion. Mirrors anyclip's
        // `used = existing | {top for _rel, top in plan}`.
        var used = existing.union(plan.map(\.top))
        var placed = PlacedFiles()
        var tops: [URL] = []
        var seenTops = Set<String>()
        for (i, item) in plan.enumerated() {
            var rel = item.relativePath
            var top = item.top
            var isFolder = item.inTree
            do {
                try writeEntry(files[i].data, rel: rel, root: root, makeDirs: item.inTree)
            } catch {
                // A destination that escapes received/, that a directory already
                // occupies, or that simply will not open costs THAT entry its
                // path — never the rest of the clip, and never the entry itself.
                AnyLog.shared.warning(
                    "received path '\(rel)' not writable (\(error)); placing flat")
                rel = uniquifyName(sanitizeFilename(files[i].name), used: &used)
                top = rel
                isFolder = false
                do {
                    try writeEntry(files[i].data, rel: rel, root: root, makeDirs: false)
                } catch {
                    AnyLog.shared.warning("file write for '\(rel)' failed: \(error); entry skipped")
                    continue
                }
            }
            placed.files.append((name: rel, data: files[i].data))
            if seenTops.insert(top).inserted {
                tops.append(root.appendingPathComponent(top))
                placed.topLevelItems.append(top)
                if isFolder { placed.folderTops.append(top) }
            }
        }
        guard !tops.isEmpty else { return empty }
        return (placed, tops, fingerprints(for: tops))
    }

    /// Write one planned entry, creating its intermediate directories first.
    /// Throws (never drops) so the caller can demote the entry to a flat name.
    private nonisolated static func writeEntry(
        _ data: Data, rel: String, root: URL, makeDirs: Bool
    ) throws {
        // Fold the components by hand: appendingPathComponent would treat
        // "docs/sub/b.txt" as one component to escape.
        let dest = ReceivedTree.pathSegments(rel)
            .reduce(root) { $0.appendingPathComponent($1) }
            .standardizedFileURL
        // Lexical guard first — cheap, and it never touches the filesystem.
        guard dest.path.hasPrefix(root.path + "/") else {
            throw ReceivedPathEscape(path: dest.path)
        }
        if makeDirs {
            try FileManager.default.createDirectory(
                at: dest.deletingLastPathComponent(), withIntermediateDirectories: true)
        }
        try writeUnder(data, to: dest, root: root)
    }

    /// Write `data` at `dest`, refusing to leave `root`.
    ///
    /// A symlink sitting AT the destination is REMOVED rather than followed:
    /// received/ is our own scratch directory, and Data.write would otherwise
    /// open through the link and drop peer bytes outside received/. The
    /// containment check is then re-run on the REAL path (realpath of the
    /// parent, which exists by now) — a lexical check cannot see an
    /// INTERMEDIATE symlink pointing out of received/. Throws when the
    /// destination escapes or the write fails; either way the caller falls back
    /// to a flat name, it never drops the entry. Port of anyclip._write_under.
    nonisolated static func writeUnder(_ data: Data, to dest: URL, root: URL) throws {
        var info = stat()
        if lstat(dest.path, &info) == 0, (info.st_mode & S_IFMT) == S_IFLNK {
            AnyLog.shared.warning("removing symlink in the way of \(dest.path)")
            try FileManager.default.removeItem(at: dest)
        }
        guard let rootReal = realPath(root.path),
              let parentReal = realPath(dest.deletingLastPathComponent().path),
              parentReal == rootReal || parentReal.hasPrefix(rootReal + "/")
        else { throw ReceivedPathEscape(path: dest.path) }
        try data.write(to: dest)
    }

    /// realpath(3): symlinks resolved on an EXISTING path, like Path.resolve().
    private nonisolated static func realPath(_ path: String) -> String? {
        guard let resolved = realpath(path, nil) else { return nil }
        defer { free(resolved) }
        return String(cString: resolved)
    }

    // MARK: - Pasteboard readers

    /// Stat a whole selection ONCE, expanding any folder in it, and return both
    /// halves so nothing is walked twice:
    ///   - fingerprints: every top-level item PLUS every file inside a copied
    ///     folder, so an edit deep in a tree re-triggers a send and a
    ///     just-placed tree cannot echo.
    ///   - items: the same selection in selection order, each folder carrying
    ///     the expansion the sender then reads from.
    /// Pure filesystem work: `nonisolated`, and the poll path runs it OFF the
    /// main actor. Port of anyclip.scan_selection.
    nonisolated static func scan(
        _ urls: [URL]
    ) -> (fingerprints: [FileFingerprint], items: [ScannedItem]) {
        var fingerprints: [FileFingerprint] = []
        var items: [ScannedItem] = []
        for url in urls {
            // A path that vanished between grab and stat drops out of both.
            guard let fp = FileFingerprint(url: url) else { continue }
            fingerprints.append(fp)
            guard fp.isDirectory else {
                items.append(ScannedItem(url: url, size: fp.size, entries: nil))
                continue
            }
            let entries = FolderExpander.walk(url)
            for entry in entries {
                if let sub = FileFingerprint(url: entry.url) { fingerprints.append(sub) }
            }
            items.append(ScannedItem(url: url, size: fp.size, entries: entries))
        }
        return (fingerprints, items)
    }

    /// The fingerprint half of `scan`, for the two baselines (startup and
    /// inbound placement) that have no use for the expansion. The comparison
    /// and both baselines go through the same scan, so the two sides always see
    /// the same shape. Port of anyclip.fingerprint_paths.
    nonisolated static func fingerprints(for urls: [URL]) -> [FileFingerprint] {
        scan(urls).fingerprints
    }

    /// Filesystem work never runs on the main actor: a 500-file tree walk or a
    /// ~49 MB read would freeze the menu bar for as long as it takes. Mirrors
    /// the asyncio.to_thread() calls Python wraps scan_selection and
    /// read_bytes in.
    private static func offMainActor<T: Sendable>(
        _ work: @escaping @Sendable () -> T
    ) async -> T {
        await Task.detached(priority: .utility) { work() }.value
    }

    static func grabFileURLs(_ pb: NSPasteboard) -> [URL] {
        let options: [NSPasteboard.ReadingOptionKey: Any] =
            [.urlReadingFileURLsOnly: true]
        let raw = pb.readObjects(forClasses: [NSURL.self], options: options)
        return (raw as? [URL]) ?? []
    }

    /// PNG bytes of an inline image, or nil.
    /// File references are handled by their own branch; grabImage returns nil
    /// when a file URL is on the clipboard (mirrors Python's behaviour).
    static func grabImage(_ pb: NSPasteboard) -> Data? {
        if !grabFileURLs(pb).isEmpty { return nil }
        if let png = pb.data(forType: .png) { return png }
        if let tiff = pb.data(forType: .tiff),
           let rep = NSBitmapImageRep(data: tiff),
           let png = rep.representation(using: .png, properties: [:]) {
            return png
        }
        return nil
    }
}
