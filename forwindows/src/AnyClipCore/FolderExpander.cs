using System.Text;

namespace AnyClip.Core;

/// Send-side folder expansion (protocol 1.3). Where the watcher used to log
/// "folder on clipboard not synced (unsupported)" it now walks the folder and
/// turns it into kind:"files" entries carrying a "path" relative to the
/// selection. Lives in Core (not the WinForms assembly) so the whole rule set
/// is covered by the platform-neutral suite. Keep in lockstep with
/// anyclip.py's expand_folder/scan_selection and Swift FolderExpander.
public static class FolderExpander
{
    /// Never synced, never counted toward the budget. Case-insensitive because
    /// the filesystems these come from are.
    public static readonly IReadOnlyList<string> JunkNames =
        new[] { ".DS_Store", "Thumbs.db", "desktop.ini" };

    private static readonly HashSet<string> Junk =
        new(JunkNames, StringComparer.OrdinalIgnoreCase);

    /// One file found inside a copied folder: where to read it from, the
    /// relative wire path it travels under (top folder name included, NFC,
    /// POSIX '/'), and the size + mtime the budget arithmetic and the change
    /// fingerprint need. Mirrors anyclip.expand_folder's
    /// (abs_path, size, mtime_ns, relpath) 4-tuple, whose mtime is what lets
    /// scan_selection fingerprint a tree without stat-ing every file twice.
    public readonly record struct WalkedFile(
        string FullPath, string RelPath, long Size, long MTimeTicks);

    /// One selection item after the single scan pass: a loose file (Entries
    /// null) or a folder carrying its expansion, so no caller walks twice.
    public sealed record ScannedItem(
        string Path, long Size, IReadOnlyList<WalkedFile>? Entries);

    /// The result of scanning a clipboard selection ONCE.
    ///  - Fingerprints: ordered (path, size, mtime) triples. A folder
    ///    contributes its OWN entry (size -1) plus one per file in its expanded
    ///    tree, so a tree we just sent (or just wrote into received/) is not
    ///    re-detected and an edit INSIDE the tree is.
    ///  - Items: the same selection in selection order, folders carrying the
    ///    expansion ExpandAsync then reads from.
    /// Port of anyclip.scan_selection / Swift ClipboardWatcher.scan.
    public sealed record ScanResult(
        IReadOnlyList<(string Path, long Size, long MTimeTicks)> Fingerprints,
        IReadOnlyList<ScannedItem> Items);

    /// One expanded selection.
    ///  - Entries: what to send, in selection order; loose files have RelPath null.
    ///  - TooLargeFolders / EmptyFolders: display names for the pinned toasts.
    ///  - SkippedFiles: LOOSE files dropped by the greedy budget/cap, i.e. the
    ///    existing "N file(s) skipped (too large to sync)" toast. A skipped
    ///    folder never lands here — it gets its own toast.
    public sealed record Plan(
        IReadOnlyList<FileEntry> Entries,
        IReadOnlyList<string> TooLargeFolders,
        IReadOnlyList<string> EmptyFolders,
        int SkippedFiles);

    /// Byte-wise comparison of two strings' UTF-8 encodings. The walk order has
    /// to be identical on all three implementations, and UTF-8 byte order is
    /// code-point order (what Python's sorted() gives). StringComparer.Ordinal
    /// compares UTF-16 code units instead, which puts astral characters BEFORE
    /// U+E000..U+FFFF (surrogates are 0xD800..0xDFFF) and would diverge.
    public static int CompareUtf8(string a, string b) =>
        Encoding.UTF8.GetBytes(a).AsSpan().SequenceCompareTo(Encoding.UTF8.GetBytes(b));

    /// Pinned toast for a folder whose expansion does not fit the clip. The
    /// wording is fixed by the design constraints and shared by all three
    /// implementations, so it lives here rather than inline in the WinForms
    /// watcher — the platform-neutral suite is the only one that runs everywhere.
    public static string TooLargeToastMessage(string folderName) =>
        $"folder too large to sync: {folderName}";

    /// Pinned toast for a selection that held one or more folders with nothing
    /// syncable in them. AGGREGATED on purpose: the wording names no folder, so
    /// ONE toast covers however many empty folders a single clip held.
    public static string EmptyToastMessage() => "folder is empty; nothing to sync";

    /// True only for a REAL symlink or junction — the thing that must never be
    /// followed. The ReparsePoint ATTRIBUTE is only a cheap PREFILTER, never the
    /// decision: on Windows it is also set on OneDrive Files On-Demand
    /// placeholders (the default Windows 11 configuration — hydrated files AND
    /// directories carry it) and on deduplicated files. Skipping on the
    /// attribute alone would walk a OneDrive-backed Documents folder as EMPTY
    /// and fire "folder is empty; nothing to sync" while the Python and Swift
    /// senders sync the same folder fine.
    ///
    /// LinkTarget is what CONFIRMS it: non-null only for a real symlink or
    /// junction (a DANGLING one included), and reading it never opens, follows
    /// or hydrates the target. That is exactly the os.path.islink() semantics
    /// (CPython >= 3.8) the Python sender inherits, so a non-link reparse point
    /// traverses as an ordinary file or directory on all three.
    internal static bool IsRealLink(string path, FileAttributes attrs)
    {
        if ((attrs & FileAttributes.ReparsePoint) == 0) return false;   // cheap prefilter
        try
        {
            FileSystemInfo info = (attrs & FileAttributes.Directory) != 0
                ? new DirectoryInfo(path)
                : new FileInfo(path);
            return info.LinkTarget is not null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot tell -> refuse to traverse. Treating an unresolvable reparse
            // point as ordinary could walk straight off the far side of a link.
            RotatingLog.Shared.Warning(
                $"folder walk: cannot resolve reparse point {path}: {e.Message}; "
                + "treating it as a link");
            return true;
        }
    }

    /// Pinned wording for an unreadable subdirectory. os.walk swallows scandir
    /// failures by default and Directory.GetFileSystemEntries throws, so
    /// without this an unreadable subtree would simply vanish and a PARTIAL
    /// tree would ship looking complete. The partial tree is still allowed
    /// (same policy as an unreadable FILE) — it is just never SILENT. Keep in
    /// lockstep with anyclip.expand_folder's onerror handler and Swift
    /// FolderExpander.collect.
    internal static string WalkErrorMessage(string path, string reason) =>
        $"folder walk error under {path}: {reason}; subtree skipped";

    /// The single choke point for "may this path go on the wire?". Returns
    /// `relPath` when it passes EVERY wire rule, or null when the file must ship
    /// as a LOOSE entry instead.
    ///
    /// The sender MUST NOT emit a path its own validator rejects, and a real
    /// filesystem can produce one: a name containing '\' (legal on macOS/Linux,
    /// reachable on Windows through a mounted share), a tree deeper than
    /// Wire.MaxPathSegments, or a sanitized path over
    /// Wire.MaxSanitizedPathLength characters. Dropping to loose keeps the file
    /// syncing and lands it exactly where the receiver would have put it anyway
    /// (an invalid path falls back to flat placement for that entry), whereas
    /// skipping would silently lose data the user asked to copy.
    /// Keep in lockstep with anyclip.py's watcher expansion and Swift
    /// FolderExpander.
    public static string? WirePathFor(string relPath, string name)
    {
        if (Wire.IsValidRelPath(relPath, name)) return relPath;
        RotatingLog.Shared.Warning(
            $"path not representable on the wire ({relPath}); "
            + $"sending {name} as a loose file");
        return null;
    }

    /// The top-level name a folder ships under (and the name used in toasts).
    /// A drive root has no basename, so it keeps its raw path.
    public static string FolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// The first segment of every wire path in this folder. Same basename as
    /// FolderDisplayName, but a root with no basename falls back to "folder"
    /// rather than its raw path — a raw "C:\" would carry a drive letter and a
    /// backslash into the path and make every entry unrepresentable. Mirrors
    /// anyclip.expand_folder's `basename(root) or "folder"` (and its separate
    /// `basename(path) or path` for the toast) and Swift's isEmpty fallback.
    private static string WalkPrefix(string root)
    {
        var name = Path.GetFileName(root.TrimEnd('/', '\\'));
        return TextHelpers.ToNfc(string.IsNullOrEmpty(name) ? "folder" : name);
    }

    /// Recursive walk of `root`: files only, symlinks never followed, junk
    /// excluded, empty directories dropped (they are not representable on the
    /// wire). RelPath is "&lt;top folder&gt;/&lt;relative path&gt;", '/'-separated and NFC
    /// per segment so the sort order is the same on every platform.
    ///
    /// This runs on EVERY clipboard change — noticing an edit deep inside a
    /// tree requires re-walking it — so `budget`/`maxFiles` are what BOUND its
    /// cost: the walk stops as soon as those absolute caps are blown, keeping
    /// deliberately ONE item past the cap, which is exactly what ExpandAsync's
    /// all-or-nothing admission needs to reject the folder. The caller's
    /// fingerprint is what suppresses the re-SEND, not this walk.
    ///
    /// RelPath here is the RAW filesystem-derived path and is NOT yet known to
    /// be wire-legal — the walk sorts on it, then ExpandAsync runs every value
    /// through WirePathFor before it becomes a FileEntry.
    public static IReadOnlyList<WalkedFile> Walk(string root, long budget, int maxFiles)
    {
        var found = new List<WalkedFile>();
        long total = 0;
        bool truncated = false;
        // LIFO with subdirectories pushed in REVERSE sorted order: this level's
        // files (byte-sorted) before its subdirectories (byte-sorted, depth
        // first), i.e. exactly os.walk's sorted top-down traversal and Swift's
        // recursion. A deterministic traversal is what makes the early-out
        // prefix below STABLE across polls; an unstable prefix would churn the
        // caller's fingerprint and re-toast the folder every cycle.
        var stack = new Stack<(string Dir, string Prefix)>();
        stack.Push((root, WalkPrefix(root)));
        while (stack.Count > 0)
        {
            var (dir, prefix) = stack.Pop();
            string[] children;
            try { children = Directory.GetFileSystemEntries(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning(WalkErrorMessage(dir, e.Message));
                continue;
            }
            var files = new List<WalkedFile>();
            var dirs = new List<(string Full, string Name)>();
            foreach (var child in children)
            {
                var name = TextHelpers.ToNfc(Path.GetFileName(child));
                FileAttributes attrs;
                // File.GetAttributes does NOT follow the link: a symlink reports
                // ReparsePoint (a dangling one included, without throwing). The
                // catch covers the enumerate-then-stat race where the entry is
                // gone by the time we look at it.
                try { attrs = File.GetAttributes(child); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning(
                        $"folder walk: stat failed for {child}: {e.Message}; skipping");
                    continue;
                }
                // Checked BEFORE the directory branch: a symlink to a folder
                // carries Directory|ReparsePoint, and descending into one would
                // both leak files from outside the selection and reintroduce
                // cycles. Attribute-prefiltered, LinkTarget-confirmed — see
                // IsRealLink for why the attribute alone is not the decision.
                if (IsRealLink(child, attrs))
                {
                    RotatingLog.Shared.Info(
                        $"folder walk: skipping symlink {child} (never followed)");
                    continue;
                }
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    dirs.Add((child, name));
                    continue;
                }
                if (Junk.Contains(name))
                {
                    RotatingLog.Shared.Debug($"folder walk: skipping junk file {child}");
                    continue;
                }
                long size, mtime;
                // A child we could not stat is DROPPED, never shipped as a
                // zero-size file — an empty file is a lie the receiver cannot
                // tell from a real one.
                try
                {
                    var info = new FileInfo(child);
                    size = info.Length;
                    mtime = info.LastWriteTimeUtc.Ticks;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning(
                        $"folder walk: stat failed for {child}: {e.Message}; skipping");
                    continue;
                }
                files.Add(new WalkedFile(child, prefix + "/" + name, size, mtime));
            }
            files.Sort((x, y) => CompareUtf8(x.RelPath, y.RelPath));
            dirs.Sort((x, y) => CompareUtf8(x.Name, y.Name));

            foreach (var file in files)
            {
                found.Add(file);
                total += file.Size;
                // ABSOLUTE-cap early-out: past maxFiles files or budget bytes a
                // folder can never fit ANY remaining budget, so there is nothing
                // to gain from walking the rest.
                if (found.Count > maxFiles || total > budget) { truncated = true; break; }
            }
            if (truncated) break;
            for (int i = dirs.Count - 1; i >= 0; i--)
                stack.Push((dirs[i].Full, prefix + "/" + dirs[i].Name));
        }
        if (truncated)
            RotatingLog.Shared.Info(
                $"folder walk: {root} is past the absolute cap "
                + $"({found.Count} files / {total} bytes); walk stopped early");
        found.Sort((x, y) => CompareUtf8(x.RelPath, y.RelPath));
        return found;
    }

    /// Stat ONE selection entry the way the change fingerprint wants it: a
    /// folder carries size -1 plus its own mtime, a file its length + mtime. A
    /// path that vanished between the clipboard grab and the stat returns null
    /// and drops out of both the fingerprint and the item list.
    private static ((string Path, long Size, long MTimeTicks) Fp, bool IsDirectory)? Stat(
        string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists) return ((path, info.Length, info.LastWriteTimeUtc.Ticks), false);
            var di = new DirectoryInfo(path);
            if (!di.Exists) return null;
            return ((path, -1, di.LastWriteTimeUtc.Ticks), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return null; }
    }

    /// Stat a clipboard selection ONCE, expanding any folder in it. Pure
    /// filesystem work and the ONLY walk per clipboard change: the caller runs
    /// it on a background thread and feeds Items straight into ExpandAsync, so
    /// nothing walks a tree twice and the UI thread never walks at all.
    /// Port of anyclip.scan_selection / Swift ClipboardWatcher.scan.
    public static ScanResult ScanSelection(
        IReadOnlyList<string> selection, long budget, int maxFiles)
    {
        var fingerprints = new List<(string Path, long Size, long MTimeTicks)>(selection.Count);
        var items = new List<ScannedItem>(selection.Count);
        foreach (var path in selection)
        {
            if (Stat(path) is not { } stat) continue;
            fingerprints.Add(stat.Fp);
            if (!stat.IsDirectory)
            {
                items.Add(new ScannedItem(path, stat.Fp.Size, null));
                continue;
            }
            var walked = Walk(path, budget, maxFiles);
            foreach (var w in walked) fingerprints.Add((w.FullPath, w.Size, w.MTimeTicks));
            items.Add(new ScannedItem(path, stat.Fp.Size, walked));
        }
        return new ScanResult(fingerprints, items);
    }

    /// Scan-and-expand in one call, for callers with no fingerprint of their
    /// own (and for the tests). The watcher uses the two halves separately so
    /// it can compare fingerprints before reading a single byte.
    public static Task<Plan> ExpandAsync(
        IReadOnlyList<string> selection, long budget, int maxFiles) =>
        ExpandAsync(ScanSelection(selection, budget, maxFiles).Items, budget, maxFiles);

    /// Expand one scanned clipboard selection. Items are processed in SELECTION
    /// order, each consuming the remaining budget/count:
    ///  - a folder is PER-FOLDER ALL-OR-NOTHING — its entire walked total must
    ///    fit what remains, or the whole folder is skipped (no partial trees);
    ///  - loose files keep today's greedy per-file behaviour.
    public static async Task<Plan> ExpandAsync(
        IReadOnlyList<ScannedItem> items, long budget, int maxFiles)
    {
        var entries = new List<FileEntry>();
        var tooLarge = new List<string>();
        var empty = new List<string>();
        int skipped = 0;
        long used = 0;

        foreach (var item in items)
        {
            if (item.Entries is { } walked)
            {
                var display = FolderDisplayName(item.Path);
                if (walked.Count == 0)
                {
                    RotatingLog.Shared.Info($"folder {display} has nothing syncable; skipping");
                    empty.Add(display);
                    continue;
                }
                long total = 0;
                foreach (var w in walked) total += w.Size;
                // Decided BEFORE any content is read.
                if (entries.Count + walked.Count > maxFiles || used + total > budget)
                {
                    RotatingLog.Shared.Info(
                        $"folder {display} skipped: {walked.Count} file(s) / {total} bytes "
                        + "do not fit the remaining budget");
                    tooLarge.Add(display);
                    continue;
                }
                long readBytes = 0;
                foreach (var w in walked)
                {
                    byte[] data;
                    // The all-or-nothing decision is made on the pre-read totals;
                    // a file that vanishes mid-read is a race, not a budget
                    // failure, so it is dropped individually rather than
                    // discarding an otherwise-good tree.
                    try { data = await File.ReadAllBytesAsync(w.FullPath); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        RotatingLog.Shared.Warning(
                            $"file read failed for {w.FullPath}: {e.Message}; "
                            + $"dropping from {display}");
                        continue;
                    }
                    readBytes += data.Length;
                    // NFC, matching the path segments the walk built: the wire
                    // rules require the last segment to equal the name EXACTLY,
                    // so a decomposed name against a composed path would send
                    // the whole file loose for no reason.
                    var leafName = TextHelpers.ToNfc(Path.GetFileName(w.FullPath));
                    // The ONLY place a path reaches the wire from this sender.
                    // A path the receiver would reject ships as a loose entry
                    // (RelPath null) instead — the file always goes.
                    entries.Add(new FileEntry(
                        leafName, data, WirePathFor(w.RelPath, leafName)));
                }
                used += readBytes;
                continue;
            }

            // Loose file: greedy, unchanged since 1.3.0. The size comes from the
            // scan, so nothing is stat-ed twice.
            if (entries.Count >= maxFiles) { skipped++; continue; }
            if (used + item.Size > budget) { skipped++; continue; }
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(item.Path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning(
                    $"file read failed for {item.Path}: {e.Message}; skipping");
                skipped++; continue;
            }
            used += item.Size;
            entries.Add(new FileEntry(Path.GetFileName(item.Path), bytes));
        }
        return new Plan(entries, tooLarge, empty, skipped);
    }
}
