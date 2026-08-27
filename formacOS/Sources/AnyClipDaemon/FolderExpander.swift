import Foundation
import AnyClipCore

/// One file found inside a copied folder: where to read it from, the relative
/// wire path it travels under (top folder name included, NFC, POSIX "/"), and
/// its raw size for the budget arithmetic.
public struct FolderFile: Equatable, Sendable {
    public let url: URL
    public let relPath: String
    public let size: Int
    public init(url: URL, relPath: String, size: Int) {
        self.url = url
        self.relPath = relPath
        self.size = size
    }
}

/// Recursive expansion of a copied folder into wire entries.
/// Keep in lockstep with anyclip.expand_folder and C# FolderExpander.Walk.
public enum FolderExpander {
    /// Never synced, never counted, log-only.
    public static let junkNames: Set<String> = [".DS_Store", "Thumbs.db", "desktop.ini"]

    /// Files only, symlinks never followed (which also makes cycles
    /// impossible), junk excluded, empty directories dropped (they are not
    /// representable on the wire). Sorted byte-wise on relPath so the same
    /// tree always produces the same clip.
    ///
    /// An unreadable subdirectory is REPORTED and skipped, not silently
    /// dropped; a partial tree is still allowed (same policy as an unreadable
    /// FILE). The walk also stops as soon as the ABSOLUTE caps are blown — see
    /// `collect`. This runs on EVERY poll (an edit deep inside a tree can only
    /// be noticed by re-walking it); the caps are what bound its cost and the
    /// caller's fingerprint is what suppresses the re-send.
    public static func walk(_ folder: URL) -> [FolderFile] {
        let top = folder.lastPathComponent.precomposedStringWithCanonicalMapping
        var state = WalkState()
        collect(dir: folder, prefix: top.isEmpty ? "folder" : top, into: &state)
        if state.truncated {
            AnyLog.shared.info(
                "folder walk: \(folder.path) is past the absolute cap "
                + "(\(state.out.count) files / \(state.total) bytes); walk stopped early")
        }
        state.out.sort { Array($0.relPath.utf8).lexicographicallyPrecedes(Array($1.relPath.utf8)) }
        return state.out
    }

    /// Running walk state: the entries so far, their raw-size total, and
    /// whether the absolute-cap early-out fired.
    private struct WalkState {
        var out: [FolderFile] = []
        var total = 0
        var truncated = false
    }

    private static func collect(dir: URL, prefix: String, into state: inout WalkState) {
        let keys: [URLResourceKey] = [.isDirectoryKey, .isSymbolicLinkKey, .fileSizeKey]
        let children: [URL]
        do {
            children = try FileManager.default.contentsOfDirectory(
                at: dir, includingPropertiesForKeys: keys, options: [])
        } catch {
            // Without this an unreadable subtree would just vanish and a
            // PARTIAL tree would ship looking complete. We keep the partial
            // tree (same policy as an unreadable FILE) but it is never SILENT.
            // Mirrors anyclip.expand_folder's os.walk onerror handler.
            AnyLog.shared.warning(
                "folder walk error under \(dir.path): \(error); subtree skipped")
            return
        }
        var files: [(url: URL, name: String, size: Int)] = []
        var dirs: [(url: URL, name: String)] = []
        for child in children {
            let name = child.lastPathComponent.precomposedStringWithCanonicalMapping
            let values = try? child.resourceValues(forKeys: Set(keys))
            // Symlink check FIRST: isDirectory follows the link, isSymbolicLink
            // does not, so this is what keeps us off the far side of a link.
            if values?.isSymbolicLink == true {
                AnyLog.shared.info("folder walk: skipping symlink \(child.path) (never followed)")
                continue
            }
            if junkNames.contains(name) {
                AnyLog.shared.debug("folder walk: skipping junk file \(child.path)")
                continue
            }
            if values?.isDirectory == true {
                dirs.append((url: child, name: name))
            } else {
                files.append((url: child, name: name, size: values?.fileSize ?? 0))
            }
        }
        // Byte-wise per directory, this level's files before its subdirectories:
        // the traversal order is then fully deterministic, which is what makes
        // the early-out prefix below STABLE across polls. An unstable prefix
        // would churn the caller's fingerprint and re-toast the folder every
        // cycle. Mirrors os.walk's sorted() + top-down recursion in Python.
        files.sort { Array($0.name.utf8).lexicographicallyPrecedes(Array($1.name.utf8)) }
        dirs.sort { Array($0.name.utf8).lexicographicallyPrecedes(Array($1.name.utf8)) }

        for file in files {
            state.out.append(FolderFile(
                url: file.url, relPath: prefix + "/" + file.name, size: file.size))
            state.total += file.size
            // ABSOLUTE-cap early-out. Past maxFilesPerClip files or fileBudget
            // bytes a folder can never fit ANY remaining budget, so there is
            // nothing to gain from walking the rest — and this walk runs on
            // EVERY poll, so an unbounded one would re-scan a huge tree forever.
            // The prefix we keep is deliberately one item PAST the cap, which is
            // exactly what the watcher's admission check needs to reject the
            // folder with the usual "folder too large to sync" toast.
            if state.out.count > ClipboardWatcher.maxFilesPerClip
                || state.total > ClipboardWatcher.fileBudget {
                state.truncated = true
                return
            }
        }
        for sub in dirs {
            collect(dir: sub.url, prefix: prefix + "/" + sub.name, into: &state)
            if state.truncated { return }
        }
    }
}
