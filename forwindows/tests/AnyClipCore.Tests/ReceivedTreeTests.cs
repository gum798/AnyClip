using System.Text;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// Receive-side tree rebuild (protocol 1.3). Entries WITHOUT a path behave
/// exactly as they did in 1.3.0; entries with one are validated and, on ANY
/// violation, fall back to flat placement for that entry alone — the frame is
/// never dropped and nothing is ever written outside received/.
/// Keep in lockstep with Swift ReceivedTreeTests + the receive half of
/// ClipboardWatcherTests, and with anyclip.plan_received_layout/update_local_files.
public class ReceivedTreeTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-recv-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    private static FileEntry E(string name, string? relPath = null, string body = "x") =>
        new(name, Encoding.UTF8.GetBytes(body), relPath);

    private static readonly Func<string, bool> NothingExists = _ => false;

    private static string Read(string root, params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { root }.Concat(parts).ToArray()));

    /// Symlinks need a privilege on Windows that a plain CI account may not
    /// have. Same soft-skip the folder-walk suite uses.
    private static bool TryLink(string path, string target, bool directory)
    {
        try
        {
            if (directory) Directory.CreateSymbolicLink(path, target);
            else File.CreateSymbolicLink(path, target);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException
            or PlatformNotSupportedException)
        { return false; }
    }

    // ---------------------------------------------------------------- Plan

    [Fact]
    public void ValidPathsRebuildOneTreeAndKeepBatchOrder()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("a.txt", "docs/a.txt"),
            E("b.txt", "docs/sub/b.txt"),
            E("loose.txt"),
        }, NothingExists);
        Assert.Equal(new[] { "docs/a.txt", "docs/sub/b.txt", "loose.txt" },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(new[] { "docs", "docs", "loose.txt" }, plan.Select(p => p.Top).ToArray());
        Assert.Equal(new[] { true, true, false }, plan.Select(p => p.InTree).ToArray());
        // One clipboard item per top-level thing, de-duped in batch order.
        Assert.Equal(new[] { "docs", "loose.txt" },
            ReceivedTree.TopLevelItems(plan).ToArray());
    }

    [Fact]
    public void AnyRuleViolationFallsBackToFlatForThatEntryOnly()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("ok.txt", "docs/ok.txt"),
            E("evil.txt", "../../etc/evil.txt"),   // traversal
            E("abs.txt", "/etc/abs.txt"),          // absolute
            E("drv.txt", "C:/Windows/drv.txt"),    // drive letter
            E("back.txt", "docs\\back.txt"),       // backslash
            E("mismatch.txt", "docs/other.txt"),   // last segment != name
            E("empty.txt", "docs//empty.txt"),     // empty segment
            E("deep.txt", string.Concat(Enumerable.Repeat("d/", 33)) + "deep.txt"),
        }, NothingExists);
        Assert.Equal(
            new[]
            {
                "docs/ok.txt", "evil.txt", "abs.txt", "drv.txt",
                "back.txt", "mismatch.txt", "empty.txt", "deep.txt",
            },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(new[] { true, false, false, false, false, false, false, false },
            plan.Select(p => p.InTree).ToArray());
        // The frame is never dropped and nothing escapes received/.
        Assert.All(plan, p => Assert.True(ReceivedTree.ResolvesUnder("/tmp/received", p.RelativePath)));
    }

    [Fact]
    public void AValidSingleSegmentPathIsALooseFile()
    {
        // "a.txt" carries no folder, so it is a loose file — not a one-segment
        // tree whose top would be uniquified with "-2" instead of " (2)".
        var plan = ReceivedTree.Plan(new[] { E("a.txt", "a.txt") }, NothingExists);
        Assert.Equal(new ReceivedTree.Placement("a.txt", "a.txt", false), plan[0]);
    }

    [Fact]
    public void TopSegmentUniquifyIsAppliedToEveryEntryOfThatFolder()
    {
        // received/docs already exists -> the WHOLE clip moves to docs-2, so one
        // copied folder always lands in exactly one new folder.
        var plan = ReceivedTree.Plan(new[]
        {
            E("a.txt", "docs/a.txt"),
            E("b.txt", "docs/sub/b.txt"),
            E("c.txt", "notes/c.txt"),
        }, top => top == "docs");
        Assert.Equal(new[] { "docs-2/a.txt", "docs-2/sub/b.txt", "notes/c.txt" },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(new[] { "docs-2", "notes" }, ReceivedTree.TopLevelItems(plan).ToArray());
    }

    [Fact]
    public void TopUniquifyKeepsBumpingAndAvoidsLooseFileNames()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("docs"),                              // a loose file literally named "docs"
            E("a.txt", "docs/a.txt"),
        }, top => top == "docs-2");                 // docs-2 already on disk
        Assert.Equal(new[] { "docs", "docs-3/a.txt" },
            plan.Select(p => p.RelativePath).ToArray());
    }

    [Fact]
    public void LooseNamesAreUniquifiedFirstAndTheCollidingTopIsWhatMoves()
    {
        // THE canonical planner order (anyclip.plan_received_layout computes
        // `uniquify_names(sorted(existing) + loose)` BEFORE it reserves any
        // top): a loose file always keeps the name the sender gave it, and the
        // folder is what gets bumped.
        var plan = ReceivedTree.Plan(new[]
        {
            E("a.txt", "docs/a.txt"),
            E("docs"),
            E("docs"),
        }, NothingExists);
        Assert.Equal(new[] { "docs-2/a.txt", "docs", "docs (2)" },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.Equal(new[] { "docs-2", "docs", "docs (2)" }, plan.Select(p => p.Top).ToArray());
    }

    [Fact]
    public void LooseNamesAreUniquifiedAgainstWhatIsAlreadyInReceived()
    {
        // received/ holds TREES now: a loose file named like a folder already
        // sitting there must be bumped, never planned straight onto the directory.
        var plan = ReceivedTree.Plan(new[] { E("docs"), E("docs") }, top => top == "docs");
        Assert.Equal(new[] { "docs (2)", "docs (3)" },
            plan.Select(p => p.RelativePath).ToArray());
        Assert.All(plan, p => Assert.False(p.InTree));
    }

    [Fact]
    public void FlatEntriesKeepTodaysWithinBatchUniquify()
    {
        var plan = ReceivedTree.Plan(new[]
        {
            E("note.txt"), E("note.txt"), E("(E&S) plan.txt"),
        }, NothingExists);
        Assert.Equal(new[] { "note.txt", "note (2).txt", "(E&S) plan.txt" },
            plan.Select(p => p.RelativePath).ToArray());
    }

    [Fact]
    public void EverySegmentIsSanitizedAndNfcIsAWireRuleNotANormalization()
    {
        var nfd = "결과".Normalize(NormalizationForm.FormD);
        var nfc = "결과".Normalize(NormalizationForm.FormC);
        var plan = ReceivedTree.Plan(new[]
        {
            E(nfd + ".txt", nfd + "/" + nfd + ".txt"),
            E("q?.txt", "docs/CON/q?.txt"),
        }, NothingExists);
        // A decomposed path is REFUSED on the wire (Wire.IsValidRelPath, Task 7),
        // so this entry lands FLAT — under a name the per-name sanitizer has
        // composed.
        Assert.Equal(nfc + ".txt", plan[0].RelativePath);
        Assert.False(plan[0].InTree);
        // A VALID path keeps its tree and every segment goes through the per-name
        // sanitizer (Windows reserved device name, denied character).
        Assert.Equal("docs/_CON/q_.txt", plan[1].RelativePath);
        Assert.True(plan[1].InTree);
    }

    [Fact]
    public void ResolvesUnderRejectsEscapes()
    {
        var root = TempDir();
        Assert.True(ReceivedTree.ResolvesUnder(root, "docs/a.txt"));
        Assert.True(ReceivedTree.ResolvesUnder(root, "a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, "../a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, "docs/../../a.txt"));
        Assert.False(ReceivedTree.ResolvesUnder(root, ""));   // resolves to root itself
    }

    // --------------------------------------------------------------- Write

    [Fact]
    public void WriteRebuildsARealTreeAndReturnsTopLevelAbsolutePaths()
    {
        var root = TempDir();
        var placed = ReceivedTree.Write(root, new[]
        {
            E("a.txt", "docs/a.txt", "aaa"),
            E("b.txt", "docs/sub/deeper/b.txt", "bbb"),
            E("loose.txt", null, "lll"),
        });
        Assert.Equal("aaa", Read(root, "docs", "a.txt"));
        Assert.Equal("bbb", Read(root, "docs", "sub", "deeper", "b.txt"));
        Assert.Equal("lll", Read(root, "loose.txt"));
        // Intermediate dirs created; clipboard gets the FOLDER once + the file.
        Assert.Equal(
            new[] { Path.Combine(root, "docs"), Path.Combine(root, "loose.txt") },
            placed.TopPaths.ToArray());
        Assert.Equal(new[] { "docs", "loose.txt" }, placed.TopLevelItems.ToArray());
        Assert.Equal(new[] { "docs" }, placed.FolderTops.ToArray());
        Assert.Equal(new[] { "docs/a.txt", "docs/sub/deeper/b.txt", "loose.txt" },
            placed.Files.Select(f => f.RelativePath).ToArray());
    }

    [Fact]
    public void WriteUniquifiesAgainstAnExistingTopFolderOnDisk()
    {
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        var placed = ReceivedTree.Write(root, new[] { E("a.txt", "docs/a.txt", "second") });
        Assert.Equal(new[] { Path.Combine(root, "docs-2") }, placed.TopPaths.ToArray());
        Assert.Equal("second", Read(root, "docs-2", "a.txt"));
    }

    [Fact]
    public void WriteRevalidatesEveryPathAndKeepsAnEscapeInsideReceived()
    {
        // The writer never trusts its caller: every RelPath is re-validated at
        // the WRITE boundary, independent of anything decode believed.
        var parent = TempDir();
        var root = Path.Combine(parent, "received");   // exclusive parent to check
        var placed = ReceivedTree.Write(root, new[] { E("evil.txt", "../../evil.txt", "x") });
        Assert.Equal(new[] { "evil.txt" }, placed.Files.Select(f => f.RelativePath).ToArray());
        Assert.Empty(placed.FolderTops);
        Assert.True(File.Exists(Path.Combine(root, "evil.txt")));
        Assert.Empty(Directory.GetFiles(parent));      // nothing climbed out
    }

    [Fact]
    public void WriteDemotesAnUnwritableEntryToAFlatNameInsteadOfDroppingIt()
    {
        // "docs/sub" arrives as a FILE, so the mkdir for "docs/sub/b.txt" cannot
        // succeed. That costs the THIRD entry its path — not the clip, and not
        // the entry: it lands flat, uniquified against everything already used
        // (the loose "b.txt" of the same batch), so one fallback can never
        // clobber another entry.
        var root = TempDir();
        var placed = ReceivedTree.Write(root, new[]
        {
            E("b.txt", null, "loose"),
            E("sub", "docs/sub", "i am a file"),
            E("b.txt", "docs/sub/b.txt", "demoted"),
        });
        Assert.Equal(new[] { "b.txt", "docs/sub", "b (2).txt" },
            placed.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal(new[] { "b.txt", "docs", "b (2).txt" }, placed.TopLevelItems.ToArray());
        Assert.Equal(new[] { "docs" }, placed.FolderTops.ToArray());
        Assert.Equal("loose", Read(root, "b.txt"));
        Assert.Equal("demoted", Read(root, "b (2).txt"));
    }

    [Fact]
    public void ALooseNameCollidingWithSomethingInReceivedIsBumpedNotWrittenOntoIt()
    {
        var root = TempDir();
        var folder = Path.Combine(root, "docs");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "keep.txt"), "kept");

        var placed = ReceivedTree.Write(root, new[] { E("docs", null, "loose docs") });

        Assert.Equal(new[] { "docs (2)" }, placed.Files.Select(f => f.RelativePath).ToArray());
        Assert.Equal("loose docs", Read(root, "docs (2)"));
        // The folder that was already there is untouched.
        Assert.Equal("kept", Read(root, "docs", "keep.txt"));
    }

    [Fact]
    public void WriteUnderRemovesALinkAtTheDestinationInsteadOfFollowingIt()
    {
        // Unreachable through Write (the planner bumps any name already in
        // received/, link or not), so the guard is exercised where it lives —
        // it is there for the plan-then-write race and for any future caller.
        var root = TempDir();
        var outside = TempDir();
        var victim = Path.Combine(outside, "victim.txt");
        File.WriteAllText(victim, "original");
        var dest = Path.Combine(root, "victim.txt");
        if (!TryLink(dest, victim, directory: false)) return;

        ReceivedTree.WriteUnder("peer bytes"u8.ToArray(), dest, root);

        // The link was removed, not followed: the peer's bytes stayed in received/.
        Assert.Equal("peer bytes", Read(root, "victim.txt"));
        Assert.Equal("original", File.ReadAllText(victim));
        Assert.Null(new FileInfo(dest).LinkTarget);
    }

    [Fact]
    public void WriteUnderRefusesADestinationThatResolvesOutsideReceived()
    {
        // An INTERMEDIATE link: the destination is lexically inside received/
        // but resolves out of it, which only the real-path re-check can catch.
        // Unreachable through Plan (an existing top is always bumped), so the
        // backstop is exercised where it lives.
        var root = TempDir();
        var outside = TempDir();
        if (!TryLink(Path.Combine(root, "escape"), outside, directory: true)) return;
        var dest = Path.Combine(root, "escape", "loot.txt");

        Assert.ThrowsAny<Exception>(() =>
            ReceivedTree.WriteUnder("loot"u8.ToArray(), dest, root));
        Assert.False(File.Exists(Path.Combine(outside, "loot.txt")));
    }

    [Fact]
    public void AReceivedTopDoesNotRatchetAcrossARestartCleanup()
    {
        // received/ holds TREES now, so the startup/shutdown sweep has to be
        // recursive: a sweep that left "docs" behind would land the same folder
        // in docs-2, docs-3, … on every restart.
        var root = TempDir();
        var clip = new[] { E("a.txt", "docs/a.txt", "one") };
        Assert.Equal(new[] { "docs" }, ReceivedTree.Write(root, clip).TopLevelItems.ToArray());
        Daemon.ClearReceivedDir(root);
        Assert.Empty(Directory.GetFileSystemEntries(root));
        Assert.Equal(new[] { "docs" }, ReceivedTree.Write(root, clip).TopLevelItems.ToArray());
    }

    // ------------------------------------------------- toast + echo seeding

    [Fact]
    public void ReceivedSummaryNamesAFolderOnlyClipAndCountsAnythingElse()
    {
        Assert.Equal("docs (2 files)", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[]
            {
                E("a.txt", "docs/a.txt"), E("b.txt", "docs/sub/b.txt"),
            })));
        // Two folders, or a folder plus a loose file, keep the plain count.
        Assert.Equal("2 files", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[]
            {
                E("a.txt", "docs/a.txt"), E("b.txt", "notes/b.txt"),
            })));
        Assert.Equal("2 files", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[] { E("a.txt", "docs/a.txt"), E("loose.txt") })));
        Assert.Equal("3 files", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[] { E("a.txt"), E("b.txt"), E("c.txt") })));
        Assert.Equal("0 files", ReceivedTree.ReceivedSummary(ReceivedTree.PlacedFiles.Empty));
    }

    [Fact]
    public void ReceivedSummaryNamesTheFolderTheClipActuallyLandedIn()
    {
        // received/docs is taken, so the clip goes to docs-2 — and the toast has
        // to say docs-2, or it points the user at somebody else's folder.
        var root = TempDir();
        Directory.CreateDirectory(Path.Combine(root, "docs"));
        Assert.Equal("docs-2 (2 files)", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(root, new[]
            {
                E("a.txt", "docs/a.txt"), E("b.txt", "docs/sub/b.txt"),
            })));
    }

    [Fact]
    public void ReceivedSummaryNeverEchoesARawWirePathIntoTheToast()
    {
        // The top segment is attacker-controlled. It reaches the summary only
        // after Plan has sanitized it. ('a|b' passes IsValidRelPath — the
        // validator constrains separators and segments, not the denylist — and
        // is then sanitized to 'a_b'. Do NOT use 'a:b' here: that trips the
        // drive-letter rule and goes flat before sanitization is reached.)
        Assert.Equal("a_b (1 files)", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[] { E("x.txt", "a|b/x.txt") })));
        // A path that fails validation is flat, so the toast names nothing.
        Assert.Equal("1 files", ReceivedTree.ReceivedSummary(
            ReceivedTree.Write(TempDir(), new[] { E("x.txt", "../../etc/x.txt") })));
    }

    [Fact]
    public void OnlyALonePlacedLooseFileSeedsTheSingleFileSuppressorSlot()
    {
        // One loose file: the watcher re-detects it as kind:"file".
        Assert.True(ReceivedTree.PlacedSingleLooseFile(
            ReceivedTree.Write(TempDir(), new[] { E("a.txt") })));
        // One FOLDER holding one file re-surfaces as kind:"files", not "file" —
        // seeding the single-file slot there would suppress an unrelated copy of
        // the same bytes. Decided on the PLACED shape, never the raw path field.
        Assert.False(ReceivedTree.PlacedSingleLooseFile(
            ReceivedTree.Write(TempDir(), new[] { E("a.txt", "docs/a.txt") })));
        Assert.False(ReceivedTree.PlacedSingleLooseFile(
            ReceivedTree.Write(TempDir(), new[] { E("a.txt"), E("b.txt") })));
        Assert.False(ReceivedTree.PlacedSingleLooseFile(ReceivedTree.PlacedFiles.Empty));
    }
}
