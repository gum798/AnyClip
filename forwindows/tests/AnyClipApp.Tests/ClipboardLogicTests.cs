using AnyClip.App;
using AnyClip.Core;
using Xunit;

namespace AnyClip.App.Tests;

internal sealed class FakeClipboard : IWin32Clipboard
{
    public string? Text;
    public byte[]? ImagePng;
    public List<string>? FilePaths;
    public bool ThrowOnRead; // simulates CLIPBRD_E_CANT_OPEN lock contention
    public List<string> Written = new();
    public string? GetText() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : Text;
    public byte[]? GetImagePng() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : ImagePng;
    public IReadOnlyList<string>? GetFilePaths() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : FilePaths;
    public bool SetText(string text) { Written.Add($"text:{text}"); Text = text; return true; }
    public bool SetImagePng(byte[] png) { Written.Add("image"); ImagePng = png; return true; }
    public bool SetFilePaths(IReadOnlyList<string> paths)
    { Written.Add($"files:{string.Join(";", paths)}"); FilePaths = paths.ToList(); return true; }
}

public class ClipboardLogicTests
{
    private static (ClipboardWatcher W, FakeClipboard C, List<ClipPayload> Changes, List<string> Skipped)
        Make(string receivedDir)
    {
        var clip = new FakeClipboard();
        var changes = new List<ClipPayload>();
        var skipped = new List<string>();
        var w = new ClipboardWatcher(clip, receivedDir)
        {
            OnLocalChange = p => { lock (changes) changes.Add(p); return Task.CompletedTask; },
            OnFileSkipped = m => { lock (skipped) skipped.Add(m); return Task.CompletedTask; },
        };
        return (w, clip, changes, skipped);
    }

    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-clip-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public async Task TextChangeFiresOnceAndEmptyIsSuppressed()
    {
        var (w, clip, changes, _) = Make(TempDir());
        clip.Text = "fresh";
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
        await w.HandleClipboardUpdateAsync(); // unchanged → no refire
        Assert.Single(changes);
        clip.Text = "";
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes); // empty not propagated
    }

    [Fact]
    public async Task PreexistingContentIsBaselined()
    {
        var dir = TempDir();
        var clip = new FakeClipboard { Text = "already there" };
        var changes = new List<ClipPayload>();
        var w = new ClipboardWatcher(clip, dir)
        { OnLocalChange = p => { changes.Add(p); return Task.CompletedTask; } };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes); // seeded at construction
    }

    [Fact]
    public async Task ImageCooldownAbsorbsSecondChange()
    {
        var (w, clip, changes, _) = Make(TempDir());
        clip.ImagePng = new byte[] { 1 };
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
        clip.ImagePng = new byte[] { 2 }; // within 1.0 s cooldown
        await w.HandleClipboardUpdateAsync();
        Assert.Single(changes);
    }

    [Fact]
    public async Task FolderSkippedOnceWithToastAndFileSent()
    {
        var dir = TempDir();
        var (w, clip, changes, skipped) = Make(dir);
        var folder = TempDir();
        clip.FilePaths = new List<string> { folder };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Single(skipped);
        Assert.Contains("folders are not supported", skipped[0]);
        await w.HandleClipboardUpdateAsync();
        Assert.Single(skipped); // fingerprint recorded → never re-detected

        var file = Path.Combine(TempDir(), "note.txt");
        File.WriteAllText(file, "file-body");
        clip.FilePaths = new List<string> { file };
        await w.HandleClipboardUpdateAsync();
        Assert.Contains(changes, c => c is FileClip f && f.Name == "note.txt");
    }

    [Fact]
    public void FileBudgetKeepsItsFormulaAgainstTheNewCap()
    {
        Assert.Equal((int)((Wire.MaxPayload - 256 * 1024) * 0.74), ClipboardWatcher.FileBudget);
        Assert.Equal(49466572, ClipboardWatcher.FileBudget); // in lockstep with Python
    }

    [Fact]
    public async Task OversizedFileSkipped()
    {
        var dir = TempDir();
        var (w, clip, changes, skipped) = Make(dir);
        var file = Path.Combine(TempDir(), "big.bin");
        // One byte past the greedy budget. Sized off the constant so the
        // boundary tracks the frame cap — a hardcoded 12 MiB was over the old
        // ~11.65 MB budget but fits the ~49.4 MB one the 64 MiB cap yields.
        // The bytes are never READ (ClipboardWatcher stats FileInfo.Length and
        // skips before ReadAllBytesAsync), but SetLength does not make the file
        // sparse on NTFS without FSCTL_SET_SPARSE, so this really does allocate
        // ~47 MB of temp disk for the duration of the test.
        using (var fs = File.Create(file)) fs.SetLength((long)ClipboardWatcher.FileBudget + 1);
        clip.FilePaths = new List<string> { file };
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Contains(skipped, s => s.Contains("1 file(s) skipped"));
    }

    [Fact]
    public async Task ApplyRemoteWritesWithoutEcho()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        Assert.True(await w.ApplyRemoteAsync(new TextClip("from peer")));
        Assert.Equal("from peer", clip.Text);
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes); // baseline updated before write

        Assert.True(await w.ApplyRemoteAsync(new FileClip("in:va/lid.txt", "x"u8.ToArray())));
        Assert.True(File.Exists(Path.Combine(dir, "lid.txt")));
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
    }

    [Fact]
    public async Task ReadFailuresWarnOnceAtThresholdAndResetOnSuccess()
    {
        var logFile = Path.Combine(TempDir(), "watch.log");
        var prev = RotatingLog.Shared;
        RotatingLog.Shared = new RotatingLog(logFile);
        try
        {
            var (w, clip, changes, _) = Make(TempDir());
            clip.ThrowOnRead = true;
            // 2 passes x 3 reads = 6 consecutive failures; the handler must
            // complete every time and warn exactly once at the threshold.
            for (int i = 0; i < 2; i++) await w.HandleClipboardUpdateAsync();
            Assert.Equal(1, CountReadFailWarnings(logFile));

            clip.ThrowOnRead = false;
            clip.Text = "recovered";
            await w.HandleClipboardUpdateAsync(); // success resets the streak
            Assert.Single(changes); // watcher still healthy after the failures

            clip.ThrowOnRead = true;
            for (int i = 0; i < 2; i++) await w.HandleClipboardUpdateAsync();
            Assert.Equal(2, CountReadFailWarnings(logFile)); // new streak warns again
        }
        finally { RotatingLog.Shared = prev; }
    }

    private static int CountReadFailWarnings(string logFile) =>
        File.ReadAllText(logFile)
            .Split("WARNING clipboard read failing").Length - 1;

    [Fact]
    public async Task OverlappingUpdatesSendFileOnlyOnce()
    {
        var clip = new FakeClipboard();
        var changes = new List<ClipPayload>();
        var gate = new TaskCompletionSource();
        var w = new ClipboardWatcher(clip, TempDir())
        {
            OnLocalChange = async p => { lock (changes) changes.Add(p); await gate.Task; },
        };
        var file = Path.Combine(TempDir(), "dup.txt");
        File.WriteAllText(file, "dup-body");
        clip.FilePaths = new List<string> { file };
        var first = w.HandleClipboardUpdateAsync();
        var second = w.HandleClipboardUpdateAsync(); // coalesced into a rerun
        gate.SetResult();
        await first;
        await second;
        lock (changes) Assert.Single(changes); // one FileClip, never two
    }

    [Fact]
    public async Task MultipleFilesEmitFilesClipWithAllEntries()
    {
        var (w, clip, changes, _) = Make(TempDir());
        var d = TempDir();
        var f1 = Path.Combine(d, "a.txt"); File.WriteAllText(f1, "aaa");
        var f2 = Path.Combine(d, "b.txt"); File.WriteAllText(f2, "bbbb");
        clip.FilePaths = new List<string> { f1, f2 };
        await w.HandleClipboardUpdateAsync();
        var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
        Assert.Equal(new[] { "a.txt", "b.txt" }, fc.Files.Select(f => f.Name).ToArray());
        Assert.Equal("aaa", System.Text.Encoding.UTF8.GetString(fc.Files[0].Data));
    }

    [Fact]
    public async Task SingleSendableFileStillEmitsLegacyFileClip()
    {
        var (w, clip, changes, _) = Make(TempDir());
        var f1 = Path.Combine(TempDir(), "solo.txt"); File.WriteAllText(f1, "x");
        clip.FilePaths = new List<string> { f1 };
        await w.HandleClipboardUpdateAsync();
        Assert.IsType<FileClip>(Assert.Single(changes));
    }

    [Fact]
    public async Task GreedyBudgetDropsOversizeKeepsFittingFilesInOrder()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var d = TempDir();
        var s1 = Path.Combine(d, "s1.txt"); File.WriteAllText(s1, "a");
        var s2 = Path.Combine(d, "s2.txt"); File.WriteAllText(s2, "b");
        var big = Path.Combine(d, "big.bin");
        using (var fs = File.Create(big)) fs.SetLength((long)ClipboardWatcher.FileBudget + 1);
        clip.FilePaths = new List<string> { s1, big, s2 }; // big in the middle
        await w.HandleClipboardUpdateAsync();
        var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
        Assert.Equal(new[] { "s1.txt", "s2.txt" }, fc.Files.Select(f => f.Name).ToArray());
        Assert.Contains(skipped, s => s.Contains("1 file"));
    }

    [Fact]
    public async Task FolderMixedWithFilesSkipsFolderSyncsFiles()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var d = TempDir();
        var folder = TempDir();
        var f1 = Path.Combine(d, "keep.txt"); File.WriteAllText(f1, "k");
        var f2 = Path.Combine(d, "keep2.txt"); File.WriteAllText(f2, "k2");
        clip.FilePaths = new List<string> { folder, f1, f2 };
        await w.HandleClipboardUpdateAsync();
        var fc = Assert.IsType<FilesClip>(Assert.Single(changes));
        Assert.Equal(2, fc.Files.Count);
        // Single folder -> exactly one skip callback, singular wording with the name.
        Assert.Equal($"folder not synced — folders are not supported: {Path.GetFileName(folder)}",
            Assert.Single(skipped));
    }

    [Fact]
    public async Task MultipleFoldersEmitOneAggregatedSkip()
    {
        var (w, clip, changes, skipped) = Make(TempDir());
        var d = TempDir();
        var folder1 = TempDir();
        var folder2 = TempDir();
        var f1 = Path.Combine(d, "keep.txt"); File.WriteAllText(f1, "k");
        clip.FilePaths = new List<string> { folder1, folder2, f1 };
        await w.HandleClipboardUpdateAsync();
        // The single accepted file still syncs (legacy FileClip kind).
        Assert.IsType<FileClip>(Assert.Single(changes));
        // Exactly ONE aggregated skip notification, plural wording, no folder names.
        Assert.Equal("2 folders not synced — folders are not supported", Assert.Single(skipped));
    }

    [Fact]
    public async Task ApplyRemoteFilesClipWritesAllUniquifiesPlacesAllNoEcho()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        var payload = new FilesClip(new List<(string, byte[])>
        {
            ("note.txt", "one"u8.ToArray()),
            ("note.txt", "two"u8.ToArray()),       // same sanitized name -> uniquified
            ("(E&S) plan.txt", "three"u8.ToArray()),
        });
        Assert.True(await w.ApplyRemoteAsync(payload));
        Assert.True(File.Exists(Path.Combine(dir, "note.txt")));
        Assert.True(File.Exists(Path.Combine(dir, "note (2).txt")));
        Assert.True(File.Exists(Path.Combine(dir, "(E&S) plan.txt")));
        Assert.Equal("three", File.ReadAllText(Path.Combine(dir, "(E&S) plan.txt")));
        Assert.Contains(clip.Written, x => x.StartsWith("files:")
            && x.Contains("note.txt") && x.Contains("note (2).txt") && x.Contains("(E&S) plan.txt"));
        // Baseline set to placed paths -> re-detect does not echo.
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
    }
}
