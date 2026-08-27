namespace AnyClip.Core;

/// Receive-side placement for a kind:"files" clip (protocol 1.3). Entries
/// without a "path" behave exactly as they did in 1.3.0; entries with one are
/// validated against every wire rule and, on ANY violation, fall back to flat
/// placement for THAT entry — the frame is never dropped and nothing is ever
/// written outside received/. Lives in Core (not the WinForms assembly) so the
/// whole rebuild is covered by the platform-neutral suite; the App layer only
/// puts the returned paths on the clipboard. Keep in lockstep with
/// anyclip.plan_received_layout/update_local_files and Swift ReceivedTree +
/// ClipboardWatcher.writeInbound.
public static class ReceivedTree
{
    /// Where ONE entry lands, relative to received/.
    ///  - RelativePath: '/'-joined SANITIZED segments; the writer swaps in the
    ///    platform separator.
    ///  - Top: the clipboard item this entry belongs to — the top folder for a
    ///    tree entry, the file itself for a loose one. Repeats across entries of
    ///    one folder; TopLevelItems de-dupes it in batch order.
    ///  - InTree: true when the entry landed inside a rebuilt folder. What tells
    ///    "one copied folder" apart from "one loose file" for the toast and for
    ///    the single-file echo-suppressor slot.
    public readonly record struct Placement(string RelativePath, string Top, bool InTree);

    /// One entry that actually reached the disk: the path it landed on relative
    /// to received/ (post-fallback, so not necessarily the planned one) and the
    /// bytes that were written.
    public sealed record PlacedFile(string RelativePath, byte[] Data);

    /// What one inbound clip actually landed as.
    ///  - TopPaths: ABSOLUTE top-level paths in batch order — exactly what the
    ///    platform puts on the clipboard (CF_HDROP on Windows).
    ///  - TopLevelItems: the same items as names under received/.
    ///  - FolderTops: the subset of those that are rebuilt FOLDERS.
    ///  - Files: every entry written, in batch order.
    /// Port of Swift PlacedFiles.
    public sealed record PlacedFiles(
        IReadOnlyList<string> TopPaths,
        IReadOnlyList<string> TopLevelItems,
        IReadOnlyList<string> FolderTops,
        IReadOnlyList<PlacedFile> Files)
    {
        public static readonly PlacedFiles Empty = new(
            Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<PlacedFile>());
    }

    /// Plan the whole clip. `topExists` answers "is there already something
    /// called this directly under received/?" — injected so the planner stays
    /// pure and testable.
    ///
    /// Rules:
    ///  - No path, or a path that violates ANY wire rule -> FLAT placement under
    ///    the sanitized name. An entry is never dropped and never escapes
    ///    received/ (validation rejects "..", and SanitizeFilename would map a
    ///    surviving ".." to "received.bin" anyway).
    ///  - A valid SINGLE-SEGMENT path is a loose file, not a tree.
    ///  - LOOSE names are uniquified FIRST (" (2)"), against each other and
    ///    against what is already on disk, so a loose file keeps the name the
    ///    sender gave it.
    ///  - Tree tops are reserved AFTER that, in first-appearance order, against
    ///    those loose names and the disk, and uniquified as "&lt;top&gt;-2",
    ///    "&lt;top&gt;-3" …; every entry sharing a top gets the SAME replacement, so
    ///    one clip lands in ONE new folder.
    ///
    /// That ORDER is canonical, not incidental: anyclip.plan_received_layout
    /// computes `uniquify_names(sorted(existing) + loose)[len(existing):]` and
    /// only then walks the tops against `used = existing | set(loose)`. A clip
    /// of [docs/a.txt, loose "docs"] therefore lands as ["docs-2/a.txt", "docs"]
    /// — the TOP moves, never the loose file.
    public static IReadOnlyList<Placement> Plan(
        IReadOnlyList<FileEntry> files, Func<string, bool> topExists)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);   // names spoken for
        var flat = new string?[files.Count];                      // batch index -> loose name
        var segments = new IReadOnlyList<string>?[files.Count];

        // Pass 1: the loose names, in batch order. Anything without a path, with
        // a path that violates a wire rule, or with a single-segment path is
        // loose. Validation runs on the RAW wire value (Ordinal, NFC-required);
        // sanitization happens only after it passes.
        for (int i = 0; i < files.Count; i++)
        {
            if (files[i].RelPath is { } raw && Wire.IsValidRelPath(raw, files[i].Name))
            {
                var parts = TextHelpers.SanitizePathSegments(raw);
                if (parts.Count >= 2) { segments[i] = parts; continue; }
            }
            flat[i] = TextHelpers.UniquifyName(
                TextHelpers.SanitizeFilename(files[i].Name), used, topExists);
        }

        // Pass 2: reserve the tree tops against the loose names AND the disk.
        // ONE replacement per DISTINCT top segment, in first-appearance order,
        // so every entry of one copied folder lands in the SAME new folder even
        // when the name had to be bumped.
        var topMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var s in segments)
        {
            if (s is null) continue;
            var wireTop = s[0];
            if (topMap.ContainsKey(wireTop)) continue;
            var candidate = wireTop;
            int n = 2;
            while (used.Contains(candidate) || topExists(candidate))
                candidate = $"{wireTop}-{n++}";
            used.Add(candidate);
            topMap[wireTop] = candidate;
        }

        // Pass 3: emit placements in the clip's own order.
        var result = new List<Placement>(files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            if (segments[i] is { } s)
            {
                var parts = s.ToArray();
                parts[0] = topMap[s[0]];
                result.Add(new Placement(string.Join("/", parts), parts[0], true));
                continue;
            }
            var name = flat[i]!;
            result.Add(new Placement(name, name, false));
        }
        return result;
    }

    /// The clip's top-level items in batch order: each copied folder once, plus
    /// every loose file. This is what goes on the clipboard.
    public static IReadOnlyList<string> TopLevelItems(IReadOnlyList<Placement> placements)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<string>();
        foreach (var p in placements) if (seen.Add(p.Top)) items.Add(p.Top);
        return items;
    }

    /// LEXICAL traversal backstop: true only when `relativePath` resolves
    /// strictly INSIDE `root`, ignoring links. Sanitization already strips '/',
    /// '\' and '..' from every segment, so this cannot fail on a planned path —
    /// it is the cheap second lock, checked again before every write. The link-
    /// aware check that a lexical test CANNOT do lives in WriteUnder.
    public static bool ResolvesUnder(string root, string relativePath)
    {
        string rootFull;
        try { rootFull = Path.GetFullPath(root); }
        catch (ArgumentException) { return false; }
        if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
            rootFull += Path.DirectorySeparatorChar;
        string full;
        try
        {
            full = Path.GetFullPath(Path.Combine(rootFull,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
        }
        catch (ArgumentException) { return false; }
        return full.Length > rootFull.Length
            && full.StartsWith(rootFull, PathComparison);
    }

    /// "Is there already something called this directly under received/?" — the
    /// ONE definition of a taken top-level name, so the planner and the writer
    /// can never disagree about whether a name had to be bumped. Directories
    /// count: received/ holds TREES now.
    public static Func<string, bool> TopExistsIn(string receivedDir) =>
        top => Directory.Exists(Path.Combine(receivedDir, top))
            || File.Exists(Path.Combine(receivedDir, top));

    /// Write one received files clip under `receivedDir`, creating intermediate
    /// directories, and return what actually landed. IO exceptions from the
    /// received/ directory itself propagate to the caller's narrow catch; a
    /// failure on ONE entry never does — it is demoted to a flat name instead.
    ///
    /// The plan is recomputed HERE from the raw entries: the writer re-validates
    /// every wire path at the write boundary rather than trusting what decode
    /// (or any other caller) concluded. Port of anyclip.update_local_files.
    public static PlacedFiles Write(string receivedDir, IReadOnlyList<FileEntry> files)
    {
        Directory.CreateDirectory(receivedDir);
        // What received/ already holds. Names, not stats: "taken" covers
        // directories too, so a colliding tree top is bumped to "<top>-2" and a
        // loose file that would land ON one of them gets " (2)".
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in Directory.GetFileSystemEntries(receivedDir))
            existing.Add(Path.GetFileName(entry));
        var onDisk = TopExistsIn(receivedDir);
        var plan = Plan(files, name => existing.Contains(name) || onDisk(name));
        // Every name already spoken for, so a demoted entry (below) can clobber
        // neither a planned one nor another demotion.
        var used = new HashSet<string>(existing, StringComparer.Ordinal);
        foreach (var p in plan) used.Add(p.Top);

        var topPaths = new List<string>();
        var tops = new List<string>();
        var folderTops = new List<string>();
        var placed = new List<PlacedFile>(files.Count);
        var seenTops = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < plan.Count; i++)
        {
            var (rel, top, inTree) = (plan[i].RelativePath, plan[i].Top, plan[i].InTree);
            try { WriteEntry(files[i].Data, rel, receivedDir, makeDirs: inTree); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException
                or NotSupportedException or ArgumentException)
            {
                // A destination that escapes received/, that a directory already
                // occupies, or that simply will not open costs THAT entry its
                // path — never the rest of the clip, and never the entry itself.
                RotatingLog.Shared.Warning(
                    $"received path '{rel}' not writable ({e.Message}); placing flat");
                rel = TextHelpers.UniquifyName(
                    TextHelpers.SanitizeFilename(files[i].Name), used);
                top = rel;
                inTree = false;
                try { WriteEntry(files[i].Data, rel, receivedDir, makeDirs: false); }
                catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException
                    or NotSupportedException or ArgumentException)
                {
                    RotatingLog.Shared.Warning(
                        $"file write for '{rel}' failed: {e2.Message}; entry skipped");
                    continue;
                }
            }
            placed.Add(new PlacedFile(rel, files[i].Data));
            if (!seenTops.Add(top)) continue;
            tops.Add(top);
            topPaths.Add(Path.Combine(receivedDir, top));
            if (inTree) folderTops.Add(top);
        }
        if (topPaths.Count == 0) return PlacedFiles.Empty;
        return new PlacedFiles(topPaths, tops, folderTops, placed);
    }

    /// Write one planned entry, creating its intermediate directories first.
    /// Throws (never drops) so the caller can demote the entry to a flat name.
    private static void WriteEntry(byte[] data, string rel, string root, bool makeDirs)
    {
        // Lexical guard first — cheap, and it never touches the filesystem.
        if (!ResolvesUnder(root, rel))
            throw new IOException($"refusing out-of-tree destination '{rel}'");
        var dest = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
        if (makeDirs) Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        WriteUnder(data, dest, root);
    }

    /// Write `data` at `dest`, refusing to leave `root`.
    ///
    /// A symlink or junction sitting AT the destination is REMOVED rather than
    /// followed: received/ is our own scratch directory, and File.WriteAllBytes
    /// would otherwise open THROUGH the link and drop peer bytes outside
    /// received/. The containment check is then re-run on the REAL path (the
    /// parent, which exists by now, with every link in its chain resolved) — a
    /// lexical check cannot see an INTERMEDIATE link pointing out of received/.
    /// Throws when the destination escapes or the write fails; either way the
    /// caller falls back to a flat name, it never drops the entry.
    /// Port of anyclip._write_under / Swift ClipboardWatcher.writeUnder.
    internal static void WriteUnder(byte[] data, string dest, string root)
    {
        if (TryGetAttributes(dest) is { } attrs && FolderExpander.IsRealLink(dest, attrs))
        {
            RotatingLog.Shared.Warning($"removing link in the way of {dest}");
            // Deleting a link never follows it: for a link TO a directory the
            // link itself is what goes.
            if ((attrs & FileAttributes.Directory) != 0) Directory.Delete(dest);
            else File.Delete(dest);
        }
        var rootReal = RealPath(root);
        var parentReal = RealPath(Path.GetDirectoryName(dest)!);
        if (rootReal is null || parentReal is null
            || !(string.Equals(parentReal, rootReal, PathComparison)
                || parentReal.StartsWith(rootReal + Path.DirectorySeparatorChar, PathComparison)))
            throw new IOException($"destination resolves outside {root}: {dest}");
        File.WriteAllBytes(dest, data);
    }

    /// Attributes of `path` WITHOUT following a link (File.GetAttributes reports
    /// the link itself), or null when nothing is there.
    private static FileAttributes? TryGetAttributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        { return null; }
    }

    /// realpath(3) for an EXISTING path: resolves EVERY component, so a link in
    /// the middle of the chain is visible. .NET has no realpath — ResolveLinkTarget
    /// only resolves the FINAL component — so the chain is walked by hand.
    /// Returns null when a component cannot be resolved, which the caller treats
    /// as "not provably inside received/" and refuses.
    private static string? RealPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        var pathRoot = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(pathRoot)) return null;
        var parts = full[pathRoot.Length..].Split(
            new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
            StringSplitOptions.RemoveEmptyEntries);
        var current = pathRoot;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            try
            {
                // returnFinalTarget walks a chain of links in one call; a
                // relative target is resolved against the link's own directory,
                // so FullName is always an absolute real path.
                if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true)
                    is { } target)
                    current = target.FullName;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { return null; }
        }
        return current;
    }

    /// NTFS is case-insensitive, so a case-differing prefix names the SAME
    /// directory and must not be read as an escape; elsewhere paths are bytes.
    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// Body of the "AnyClip ← peer" toast for a files clip: a clip that is
    /// entirely ONE copied folder names it, anything else keeps today's count.
    ///
    /// Derived from what was PLACED — the sanitized, uniquified name the clip
    /// actually landed under — never from the raw wire path. Naming the raw top
    /// segment would point the user at `docs` when a collision put the clip in
    /// `docs-2`, and would put attacker-controlled text straight into a
    /// notification. Keep in lockstep with Swift receivedFilesBody and
    /// anyclip.received_clip_message.
    public static string ReceivedSummary(PlacedFiles placed)
    {
        if (placed.FolderTops.Count == 1 && placed.TopLevelItems.Count == 1)
            return $"{placed.FolderTops[0]} ({placed.Files.Count} files)";
        return $"{placed.Files.Count} files";
    }

    /// True when a received files clip ended up as exactly ONE placed top-level
    /// item and that item is a LOOSE file rather than a folder — the only case
    /// in which the watcher re-detects it as a single-file copy (kind "file")
    /// and the daemon has to seed that suppressor slot too. A placed FOLDER
    /// re-surfaces as kind:"files" and needs no extra seeding.
    /// Keep in lockstep with Swift placedSingleLooseFile /
    /// anyclip.placed_single_loose_file.
    public static bool PlacedSingleLooseFile(PlacedFiles placed) =>
        placed.TopLevelItems.Count == 1 && placed.FolderTops.Count == 0;
}
