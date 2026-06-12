using AnyClip.App;
using AnyClip.Core;
using Xunit;

namespace AnyClip.App.Tests;

internal sealed class FakeClipboard : IWin32Clipboard
{
    public string? Text;
    public byte[]? ImagePng;
    public string? FilePath;
    public bool ThrowOnRead; // simulates CLIPBRD_E_CANT_OPEN lock contention
    public List<string> Written = new();
    public string? GetText() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : Text;
    public byte[]? GetImagePng() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : ImagePng;
    public string? GetFirstFilePath() =>
        ThrowOnRead ? throw new InvalidOperationException("clipboard locked") : FilePath;
    public bool SetText(string text) { Written.Add($"text:{text}"); Text = text; return true; }
    public bool SetImagePng(byte[] png) { Written.Add("image"); ImagePng = png; return true; }
    public bool SetFilePath(string path) { Written.Add($"file:{path}"); FilePath = path; return true; }
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
        clip.FilePath = folder;
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
        Assert.Single(skipped);
        Assert.Contains("folders are not supported", skipped[0]);
        await w.HandleClipboardUpdateAsync();
        Assert.Single(skipped); // fingerprint recorded → never re-detected

        var file = Path.Combine(TempDir(), "note.txt");
        File.WriteAllText(file, "file-body");
        clip.FilePath = file;
        await w.HandleClipboardUpdateAsync();
        Assert.Contains(changes, c => c is FileClip f && f.Name == "note.txt");
    }

    [Fact]
    public async Task OversizedFileSkipped()
    {
        var dir = TempDir();
        var (w, clip, changes, _) = Make(dir);
        var file = Path.Combine(TempDir(), "big.bin");
        using (var fs = File.Create(file)) fs.SetLength(12L * 1024 * 1024);
        clip.FilePath = file;
        await w.HandleClipboardUpdateAsync();
        Assert.Empty(changes);
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
        clip.FilePath = file;
        var first = w.HandleClipboardUpdateAsync();
        var second = w.HandleClipboardUpdateAsync(); // coalesced into a rerun
        gate.SetResult();
        await first;
        await second;
        lock (changes) Assert.Single(changes); // one FileClip, never two
    }
}
