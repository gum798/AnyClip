using System.Text;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// Send-side folder expansion (protocol 1.3). Runs on the platform-neutral
/// suite because FolderExpander only touches System.IO — the WinForms watcher
/// just hands it the clipboard's file-drop list.
public class FolderExpanderTests
{
    // The watcher's budget constant lives in the WinForms assembly, which this
    // platform-neutral suite cannot reference; the formula is pinned there
    // (ClipboardWatcher.FileBudget == (int)((Wire.MaxPayload - 256*1024) * 0.74)).
    private const long ClipboardWatcher_FileBudget = 49_466_572;

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-expand-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Write(string dir, string relative, string body)
    {
        var full = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, body);
        return full;
    }

    private static string MakeTree(string name)
    {
        var root = Path.Combine(TempDir(), name);
        Directory.CreateDirectory(root);
        return root;
    }

    [Fact]
    public async Task FolderExpandsIntoPathCarryingEntriesInByteSortedOrder()
    {
        var root = MakeTree("docs");
        Write(root, "b.txt", "bbb");
        Write(root, "a.txt", "aaa");
        Write(root, "sub/z.txt", "zzz");
        Write(root, "sub/deeper/y.txt", "yyy");

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        Assert.Empty(plan.TooLargeFolders);
        Assert.Empty(plan.EmptyFolders);
        Assert.Equal(0, plan.SkippedFiles);
        // Deterministic byte-wise sort on the relative path, top name included.
        Assert.Equal(
            new[] { "docs/a.txt", "docs/b.txt", "docs/sub/deeper/y.txt", "docs/sub/z.txt" },
            plan.Entries.Select(e => e.RelPath).ToArray());
        Assert.Equal(new[] { "a.txt", "b.txt", "y.txt", "z.txt" },
            plan.Entries.Select(e => e.Name).ToArray());
        Assert.Equal("aaa", Encoding.UTF8.GetString(plan.Entries[0].Data));
    }

    [Fact]
    public async Task JunkFilesAndEmptyDirsAreExcluded()
    {
        var root = MakeTree("mixed");
        Write(root, "keep.txt", "k");
        Write(root, ".DS_Store", "junk");
        Write(root, "Thumbs.db", "junk");
        Write(root, "sub/desktop.ini", "junk");
        Directory.CreateDirectory(Path.Combine(root, "empty-dir"));

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // Empty dirs are not representable and are dropped; junk never ships.
        Assert.Equal(new[] { "mixed/keep.txt" }, plan.Entries.Select(e => e.RelPath).ToArray());
    }

    [Fact]
    public async Task SymlinksAreExcludedAndNeverFollowed()
    {
        var root = MakeTree("linked");
        Write(root, "keep.txt", "k");
        var outside = Path.Combine(TempDir(), "outside.txt");
        File.WriteAllText(outside, "secret");
        // Split out of the junk fact and privilege-gated ON PURPOSE. This suite
        // is the platform-neutral one, and release.yml runs it on
        // windows-latest, where creating a symlink needs
        // SeCreateSymbolicLinkPrivilege / Developer Mode and otherwise throws
        // UnauthorizedAccessException or IOException. Turning the release job
        // red over a missing privilege would be a failure unrelated to folder
        // sync, so the fact no-ops when the link cannot be created. Detection
        // itself is platform-neutral: File.GetAttributes reports ReparsePoint
        // for live AND dangling links without following them.
        try { File.CreateSymbolicLink(Path.Combine(root, "link.txt"), outside); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        {
            return;   // no symlink privilege on this runner; nothing to assert
        }

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // The link never ships, and its target is never read — so a symlink can
        // neither leak files from outside the selection nor create a cycle.
        Assert.Equal(new[] { "linked/keep.txt" }, plan.Entries.Select(e => e.RelPath).ToArray());
        Assert.DoesNotContain(plan.Entries, e => e.Name == "link.txt");
    }

    [Fact]
    public async Task ADirectorySymlinkIsSkippedSoNothingLeaksAndCyclesAreImpossible()
    {
        var root = MakeTree("cyclic");
        Write(root, "keep.txt", "k");
        var outsideDir = MakeTree("elsewhere");
        Write(outsideDir, "secret.txt", "s");
        try
        {
            // One link OUT of the selection and one link back to the folder
            // itself: the first would leak files the user never copied, the
            // second is an infinite descent.
            Directory.CreateSymbolicLink(Path.Combine(root, "out"), outsideDir);
            Directory.CreateSymbolicLink(Path.Combine(root, "self"), root);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        {
            return;   // no symlink privilege on this runner; nothing to assert
        }

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // A directory symlink carries Directory|ReparsePoint, so the link check
        // has to run BEFORE the directory branch or the walk descends through it.
        Assert.Equal(new[] { "cyclic/keep.txt" }, plan.Entries.Select(e => e.RelPath).ToArray());
        Assert.DoesNotContain(plan.Entries, e => e.Name == "secret.txt");
    }

    [Fact]
    public void IsRealLinkTreatsTheAttributeAsAPrefilterNotAsTheDecision()
    {
        var dir = TempDir();
        var ordinary = Path.Combine(dir, "ordinary.txt");
        File.WriteAllText(ordinary, "x");

        // THE REGRESSION GUARD. Windows sets ReparsePoint on things that are not
        // links at all — OneDrive Files On-Demand placeholders (the default
        // Windows 11 configuration, hydrated files and directories alike) and
        // deduplicated files. A walk that skipped on the attribute ALONE would
        // see a OneDrive-backed Documents folder as empty and toast
        // "folder is empty; nothing to sync" while Python and Swift sync it.
        // Such a placeholder cannot be constructed on this (or any) runner, so
        // the attribute is injected instead: an ordinary file that merely CLAIMS
        // the attribute must still not count as a link.
        Assert.False(FolderExpander.IsRealLink(ordinary, FileAttributes.ReparsePoint));
        Assert.False(FolderExpander.IsRealLink(
            dir, FileAttributes.Directory | FileAttributes.ReparsePoint));

        // Without the attribute the prefilter short-circuits — no disk access at all.
        Assert.False(FolderExpander.IsRealLink(ordinary, FileAttributes.Normal));

        // And a REAL link is still a link, confirmed through LinkTarget (which
        // never opens, follows or hydrates the target).
        var link = Path.Combine(dir, "link.txt");
        try { File.CreateSymbolicLink(link, ordinary); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        { return; }
        Assert.True(FolderExpander.IsRealLink(link, File.GetAttributes(link)));

        // A DANGLING link too: LinkTarget reports the target of a broken link
        // without throwing, so a walk never has to stat through it to find out.
        var dangling = Path.Combine(dir, "dangling.txt");
        File.CreateSymbolicLink(dangling, Path.Combine(dir, "no-such-file.txt"));
        Assert.True(FolderExpander.IsRealLink(dangling, File.GetAttributes(dangling)));
    }

    [Fact]
    public void FolderToastsAreThePinnedWording()
    {
        // Constraints-pinned user-facing strings. Asserted HERE (and not only in
        // the Windows-CI-only watcher suite) so every runner checks the wording.
        Assert.Equal("folder too large to sync: docs",
            FolderExpander.TooLargeToastMessage("docs"));
        Assert.Equal("folder is empty; nothing to sync", FolderExpander.EmptyToastMessage());
    }

    [Fact]
    public void WirePathForRejectsWhatTheReceiverWouldRejectAndDropsToLoose()
    {
        // The sender MUST NOT emit a path its own validator rejects. A real
        // filesystem can produce one, so WirePathFor is the single choke point:
        // null means "ship this file as a LOOSE entry", never "drop the file".
        Assert.Equal("docs/a.txt", FolderExpander.WirePathFor("docs/a.txt", "a.txt"));

        // Deeper than 32 segments (no disk tree needed — and none is BUILT here
        // on purpose, since a 33-deep or 240-char path can blow past MAX_PATH on
        // the windows-latest runner that executes this suite).
        var deep33 = string.Join("/", Enumerable.Repeat("d", Wire.MaxPathSegments)) + "/a.txt";
        Assert.Null(FolderExpander.WirePathFor(deep33, "a.txt"));

        // Sanitized total over 240 characters.
        var long241 = new string('x', 235) + "/a.txt";
        Assert.Null(FolderExpander.WirePathFor(long241, "a.txt"));

        // A backslash in a file NAME is legal on macOS/Linux and reachable on
        // Windows via a mounted share; it is not legal on the wire.
        Assert.Null(FolderExpander.WirePathFor("docs/back\\slash.txt", "back\\slash.txt"));
    }

    [Fact]
    public async Task AnUnrepresentablePathShipsTheFileAsALooseEntry()
    {
        var root = MakeTree("deep");
        // 32 nested single-character directories puts the file at 34 segments
        // (deep/ + 32 dirs + the file) while keeping the ABSOLUTE path short
        // enough for any platform.
        var nested = string.Join("/", Enumerable.Repeat("d", 32)) + "/leaf.txt";
        Write(root, nested, "L");
        Write(root, "shallow.txt", "s");

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // Both files still ship — the over-deep one just loses its path rather
        // than being dropped, or shipping a path the receiver would reject.
        Assert.Equal(2, plan.Entries.Count);
        var leaf = Assert.Single(plan.Entries, e => e.Name == "leaf.txt");
        Assert.Null(leaf.RelPath);
        Assert.Equal("deep/shallow.txt",
            Assert.Single(plan.Entries, e => e.Name == "shallow.txt").RelPath);
        // Not counted as a skip: nothing was skipped.
        Assert.Equal(0, plan.SkippedFiles);
        Assert.Empty(plan.TooLargeFolders);
    }

    [Fact]
    public async Task OversizeFolderIsAllOrNothingAndNamesTheFolder()
    {
        var root = MakeTree("heavy");
        Write(root, "small.txt", "s");
        var big = Path.Combine(root, "big.bin");
        using (var fs = File.Create(big)) fs.SetLength(ClipboardWatcher_FileBudget + 1);

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        // No partial trees: the whole folder goes, or none of it does.
        Assert.Empty(plan.Entries);
        Assert.Equal(new[] { "heavy" }, plan.TooLargeFolders.ToArray());
        Assert.Equal(0, plan.SkippedFiles);
    }

    [Fact]
    public async Task FolderIsAllOrNothingAgainstTheREMAININGCountToo()
    {
        var root = MakeTree("three");
        Write(root, "a.txt", "a");
        Write(root, "b.txt", "b");
        Write(root, "c.txt", "c");
        var loose = Path.Combine(TempDir(), "loose.txt");
        File.WriteAllText(loose, "l");

        // Cap 3, and the loose file (processed FIRST, in selection order)
        // consumes one slot -> the 3-file folder no longer fits at all.
        var plan = await FolderExpander.ExpandAsync(
            new[] { loose, root }, ClipboardWatcher_FileBudget, 3);
        Assert.Equal(new[] { "loose.txt" }, plan.Entries.Select(e => e.Name).ToArray());
        Assert.Equal(new[] { "three" }, plan.TooLargeFolders.ToArray());

        // Same selection with room for all four: the folder is taken whole.
        var roomy = await FolderExpander.ExpandAsync(
            new[] { loose, root }, ClipboardWatcher_FileBudget, 4);
        Assert.Equal(4, roomy.Entries.Count);
        Assert.Empty(roomy.TooLargeFolders);
    }

    [Fact]
    public async Task EmptyFolderIsReportedAndSendsNothing()
    {
        var root = MakeTree("hollow");
        Write(root, "nested/.DS_Store", "junk");   // nothing left after exclusions

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 500);

        Assert.Empty(plan.Entries);
        Assert.Empty(plan.TooLargeFolders);
        Assert.Equal(new[] { "hollow" }, plan.EmptyFolders.ToArray());
    }

    [Fact]
    public async Task LooseFilesCarryNoPathAndKeepTodaysGreedyBehaviour()
    {
        var d = TempDir();
        var s1 = Write(d, "s1.txt", "a");
        var big = Path.Combine(d, "big.bin");
        using (var fs = File.Create(big)) fs.SetLength(ClipboardWatcher_FileBudget + 1);
        var s2 = Write(d, "s2.txt", "b");

        var plan = await FolderExpander.ExpandAsync(
            new[] { s1, big, s2 }, ClipboardWatcher_FileBudget, 500);

        // Greedy, per file, in selection order — unchanged from 1.3.0.
        Assert.Equal(new[] { "s1.txt", "s2.txt" }, plan.Entries.Select(e => e.Name).ToArray());
        Assert.All(plan.Entries, e => Assert.Null(e.RelPath));
        Assert.Equal(1, plan.SkippedFiles);
    }

    [Fact]
    public async Task SelectionOrderIsHonouredAcrossFoldersAndFiles()
    {
        var one = MakeTree("one");
        Write(one, "x.txt", "x");
        var two = MakeTree("two");
        Write(two, "y.txt", "y");
        var loose = Path.Combine(TempDir(), "mid.txt");
        File.WriteAllText(loose, "m");

        var plan = await FolderExpander.ExpandAsync(
            new[] { one, loose, two }, ClipboardWatcher_FileBudget, 500);

        // Each folder keeps its OWN top name; loose files stay path-free.
        Assert.Equal(new string?[] { "one/x.txt", null, "two/y.txt" },
            plan.Entries.Select(e => e.RelPath).ToArray());
    }

    [Fact]
    public void Utf8ByteOrderIsUsedNotUtf16CodeUnitOrder()
    {
        // U+1F600 encodes to F0 9F 98 80 and U+FFFD to EF BF BD, so UTF-8 byte
        // order (== code-point order, what Python's sorted() gives) puts the
        // emoji AFTER. UTF-16 code-unit order puts it BEFORE, because the lead
        // surrogate is 0xD83D < 0xFFFD. A folder with an emoji-named file would
        // otherwise ship in a different order from the Python/Swift senders.
        Assert.True(FolderExpander.CompareUtf8("\U0001F600", "\uFFFD") > 0);
        Assert.True(string.CompareOrdinal("\U0001F600", "\uFFFD") < 0);
        Assert.True(FolderExpander.CompareUtf8("a", "b") < 0);
        Assert.Equal(0, FolderExpander.CompareUtf8("same", "same"));
    }

    [Fact]
    public void FolderDisplayNameHandlesTrailingSeparators()
    {
        Assert.Equal("docs", FolderExpander.FolderDisplayName("/tmp/docs"));
        Assert.Equal("docs", FolderExpander.FolderDisplayName("/tmp/docs/"));
        Assert.Equal("/", FolderExpander.FolderDisplayName("/"));   // root keeps the raw path
    }

    // ---- absolute-cap early-out (pinned walk semantics) --------------------

    [Fact]
    public void WalkStopsAtTheAbsoluteFileCapKeepingExactlyOneItemPastIt()
    {
        var root = MakeTree("many");
        for (int i = 0; i < 6; i++) Write(root, $"f{i}.txt", "x");

        var walked = FolderExpander.Walk(root, ClipboardWatcher_FileBudget, 3);

        // The walk runs on every clipboard change, so an unbounded one would
        // re-scan a huge tree forever. It keeps ONE item past the cap — exactly
        // what the admission check needs to reject the folder — and never the
        // rest of the tree.
        Assert.Equal(4, walked.Count);
        Assert.Equal(new[] { "many/f0.txt", "many/f1.txt", "many/f2.txt", "many/f3.txt" },
            walked.Select(w => w.RelPath).ToArray());
    }

    [Fact]
    public void WalkStopsAtTheAbsoluteByteBudgetToo()
    {
        var root = MakeTree("bytes");
        Write(root, "a.txt", "aaaa");
        Write(root, "b.txt", "bbbb");
        Write(root, "c.txt", "cccc");

        var walked = FolderExpander.Walk(root, budget: 5, maxFiles: 500);

        Assert.Equal(new[] { "bytes/a.txt", "bytes/b.txt" },
            walked.Select(w => w.RelPath).ToArray());
    }

    [Fact]
    public async Task AnOverCapFolderIsRejectedWholeNotShippedAsAPartialTree()
    {
        var root = MakeTree("bulk");
        for (int i = 0; i < 6; i++) Write(root, $"f{i}.txt", "x");

        var plan = await FolderExpander.ExpandAsync(
            new[] { root }, ClipboardWatcher_FileBudget, 3);

        // The truncated prefix is one PAST the cap, so all-or-nothing rejects
        // the folder with the pinned toast instead of shipping half a tree.
        Assert.Empty(plan.Entries);
        Assert.Equal(new[] { "bulk" }, plan.TooLargeFolders.ToArray());
    }

    // ---- unreadable subtrees ----------------------------------------------

    [Fact]
    public void WalkErrorMessageIsThePinnedWording()
    {
        // Pinned in lockstep with anyclip.expand_folder's os.walk onerror
        // handler and Swift FolderExpander.collect.
        Assert.Equal("folder walk error under /tmp/x: Access denied; subtree skipped",
            FolderExpander.WalkErrorMessage("/tmp/x", "Access denied"));
    }

    [Fact]
    public void AnUnreadableSubdirectoryIsSkippedWithoutLosingTheRestOfTheTree()
    {
        var root = MakeTree("guarded");
        Write(root, "top.txt", "t");
        Write(root, "secret/inside.txt", "s");
        var secret = Path.Combine(root, "secret");
        // Privilege/platform-gated like the symlink fact: there is no chmod on
        // the windows-latest runner that also executes this suite (an ACL denial
        // would be the equivalent, and setting one up is not worth a red release
        // job), and a root user reads through mode 000 anyway.
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(secret, UnixFileMode.None); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        try
        {
            try { Directory.GetFileSystemEntries(secret); return; }  // still readable
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            var walked = FolderExpander.Walk(root, ClipboardWatcher_FileBudget, 500);

            // The subtree is skipped (and logged), the siblings still ship —
            // a partial tree is allowed, it is just never SILENT.
            Assert.Equal(new[] { "guarded/top.txt" },
                walked.Select(w => w.RelPath).ToArray());
        }
        finally
        {
            File.SetUnixFileMode(secret,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // ---- one scan per clipboard change ------------------------------------

    [Fact]
    public void ScanSelectionFingerprintsTheWholeTreeAndCarriesTheExpansion()
    {
        var root = MakeTree("tracked");
        var inner = Write(root, "sub/a.txt", "one");
        var loose = Path.Combine(TempDir(), "solo.txt");
        File.WriteAllText(loose, "s");

        var first = FolderExpander.ScanSelection(
            new[] { root, loose }, ClipboardWatcher_FileBudget, 500);

        // The folder's OWN entry (size -1) plus one per walked file plus the
        // loose file: an edit deep inside a copied tree has to re-trigger, and
        // a tree we just placed must not be re-detected.
        Assert.Equal(3, first.Fingerprints.Count);
        Assert.Equal(-1, first.Fingerprints[0].Size);
        // The expansion travels with the scan, so nothing walks the tree twice.
        Assert.NotNull(first.Items[0].Entries);
        Assert.Null(first.Items[1].Entries);

        File.WriteAllText(inner, "one-and-then-some");
        var second = FolderExpander.ScanSelection(
            new[] { root, loose }, ClipboardWatcher_FileBudget, 500);
        Assert.NotEqual(first.Fingerprints, second.Fingerprints);
    }
}
