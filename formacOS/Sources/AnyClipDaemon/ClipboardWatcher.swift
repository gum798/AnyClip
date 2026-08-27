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
        // This stats the selection and re-walks every folder in it, on EVERY
        // poll — noticing an edit deep inside a tree requires that, and
        // FolderExpander bails out at the absolute caps so the cost is bounded.
        // What the comparison below saves is the re-SEND and the re-READ of an
        // unchanged selection, not the walk.
        let fingerprints = Self.fingerprints(for: urls)
        guard fingerprints != lastFileFingerprints else { return }
        // Record FIRST so the same selection is never re-detected (no retry loop),
        // regardless of what we end up sending.
        lastFileFingerprints = fingerprints

        var sendable: [(name: String, data: Data, relPath: String?)] = []
        var running = 0
        var skippedForSize = 0
        // Folders that did not fit are named one toast each (the pinned string
        // carries the folder name); empty folders share ONE generic toast.
        var oversizeFolders: [String] = []
        var emptyFolders = 0
        for url in urls {
            guard let fp = FileFingerprint(url: url) else { continue }
            if fp.isDirectory {
                let top = url.lastPathComponent.precomposedStringWithCanonicalMapping
                let entries = FolderExpander.walk(url)
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
                    guard let data = try? Data(contentsOf: entry.url) else {
                        // A read failure is per-file, exactly like a loose file:
                        // the rest of the tree still goes.
                        AnyLog.shared.warning("file read failed for \(entry.url.path); skipping")
                        continue
                    }
                    sendable.append((
                        name: entry.url.lastPathComponent.precomposedStringWithCanonicalMapping,
                        data: data, relPath: entry.relPath))
                }
                continue
            }
            // Loose files keep today's greedy per-file behaviour.
            if sendable.count >= Self.maxFilesPerClip || running + fp.size > Self.fileBudget {
                skippedForSize += 1
                continue
            }
            guard let data = try? Data(contentsOf: url) else {
                AnyLog.shared.warning("file read failed for \(url.path); skipping")
                continue
            }
            running += fp.size
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
    public func updateLocalFile(name: String, data: Data) -> Bool {
        !updateLocalFiles([(name: name, data: data, relPath: nil)]).isEmpty
    }

    /// Sanitize + uniquify, write every file into the flat receivedDir, then
    /// place ALL written URLs on the clipboard in ONE writeObjects. Returns the
    /// files actually PLACED (sanitized names) so the caller can baseline echo
    /// suppression to the placed set.
    @discardableResult
    public func updateLocalFiles(_ files: [(name: String, data: Data, relPath: String?)]) -> [(name: String, data: Data)] {
        do {
            try FileManager.default.createDirectory(
                at: receivedDir, withIntermediateDirectories: true)
        } catch {
            AnyLog.shared.warning("received dir create failed: \(error)")
            return []
        }
        let names = uniquifyNames(files.map { sanitizeFilename($0.name) })
        var placedURLs: [NSURL] = []
        var placed: [(name: String, data: Data)] = []
        for (i, f) in files.enumerated() {
            let target = receivedDir.appendingPathComponent(names[i])
            do {
                try f.data.write(to: target)
                placedURLs.append(target as NSURL)
                placed.append((name: names[i], data: f.data))
            } catch {
                AnyLog.shared.warning("file write to \(target.path) failed: \(error)")
            }
        }
        guard !placedURLs.isEmpty else { return [] }
        // Baseline the fingerprint list to the placed paths BEFORE the clipboard
        // write so a racing poll cannot echo.
        lastFileFingerprints = Self.fingerprints(for: placedURLs.map { $0 as URL })
        pasteboard.clearContents()
        let ok = pasteboard.writeObjects(placedURLs)
        lastChangeCount = pasteboard.changeCount
        if !ok { AnyLog.shared.warning("clipboard write (files) failed") }
        return placed
    }

    // MARK: - Pasteboard readers

    /// Fingerprint of a whole selection: every top-level item PLUS every file
    /// inside a copied folder, so an edit deep in a tree re-triggers a send and
    /// a just-placed tree cannot echo. The comparison and both baselines
    /// (startup, inbound placement) go through here, so the two sides always
    /// see the same shape.
    nonisolated static func fingerprints(for urls: [URL]) -> [FileFingerprint] {
        var out: [FileFingerprint] = []
        for url in urls {
            guard let fp = FileFingerprint(url: url) else { continue }
            out.append(fp)
            if fp.isDirectory {
                for entry in FolderExpander.walk(url) {
                    if let sub = FileFingerprint(url: entry.url) { out.append(sub) }
                }
            }
        }
        return out
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
