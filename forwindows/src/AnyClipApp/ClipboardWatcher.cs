using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AnyClip.Core;

namespace AnyClip.App;

/// Thin clipboard seam so the watcher logic is testable without the real
/// (flaky-on-CI) Windows clipboard.
public interface IWin32Clipboard
{
    string? GetText();
    byte[]? GetImagePng();
    IReadOnlyList<string>? GetFilePaths();
    bool SetText(string text);
    bool SetImagePng(byte[] png);
    bool SetFilePaths(IReadOnlyList<string> paths);
}

/// Real implementation over WinForms Clipboard. The WinForms Clipboard
/// requires the STA UI thread; daemon tasks call ApplyRemote off-thread,
/// so every access is marshalled through `Invoker` (a UI-thread Control
/// set by Program after startup).
public sealed class WinFormsClipboard : IWin32Clipboard
{
    /// UI-thread control used to marshal clipboard access; set once by
    /// Program. Until set, calls run on the current thread (startup
    /// baseline seeding happens on the UI thread before the daemon runs).
    public Control? Invoker { get; set; }

    private T OnSta<T>(Func<T> f)
    {
        var inv = Invoker;
        if (inv is { InvokeRequired: true }) return (T)inv.Invoke(f)!;
        return f();
    }

    public string? GetText() => OnSta(() =>
        Clipboard.ContainsText() ? Clipboard.GetText() : null);

    public byte[]? GetImagePng() => OnSta<byte[]?>(() =>
    {
        // File copies also carry thumbnails: files take priority as their
        // own kind (mirrors PIL ImageGrab returning a path list).
        if (Clipboard.ContainsFileDropList()) return null;
        if (!Clipboard.ContainsImage()) return null;
        using var image = Clipboard.GetImage();
        if (image is null) return null;
        using var ms = new MemoryStream();
        image.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    });

    public IReadOnlyList<string>? GetFilePaths() => OnSta<IReadOnlyList<string>?>(() =>
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        var list = Clipboard.GetFileDropList();
        if (list.Count == 0) return null;
        var result = new List<string>(list.Count);
        foreach (var p in list) if (p is not null) result.Add(p);
        return result.Count > 0 ? result : null;
    });

    public bool SetText(string text) => OnSta(() =>
    { try { Clipboard.SetText(text); return true; } catch (Exception) { return false; } });

    public bool SetImagePng(byte[] png) => OnSta(() =>
    {
        try
        {
            using var ms = new MemoryStream(png);
            using var image = Image.FromStream(ms);
            Clipboard.SetImage(image);
            return true;
        }
        catch (Exception) { return false; }
    });

    public bool SetFilePaths(IReadOnlyList<string> paths) => OnSta(() =>
    {
        try
        {
            var sc = new System.Collections.Specialized.StringCollection();
            foreach (var p in paths) sc.Add(p);
            Clipboard.SetFileDropList(sc);
            return true;
        }
        catch (Exception) { return false; }
    });
}

/// Clipboard-change handling with the exact baselines/cooldown/budget
/// semantics of the other ports, triggered by WM_CLIPBOARDUPDATE instead
/// of polling. Implements the daemon's IClipboardSync.
public sealed class ClipboardWatcher : IClipboardSync
{
    public const double ImageCooldownSeconds = 1.0;
    public const int ReadFailWarnAt = 5; // READ_FAIL_WARN_AT in anyclip.py
    /// Greedy send budget, applied to the SUM of raw file sizes in one clip.
    /// Reserves ~256 KB for the JSON envelope and the b64 1.34x inflation.
    /// Formula unchanged since the 16 MiB days; against the 64 MiB cap it lands
    /// at 49,466,572 (was ~12,221,153).
    /// Mirrors Python: FILE_BUDGET = int((MAX_PAYLOAD - 256*1024) * 0.74)
    public static readonly int FileBudget =
        (int)((Wire.MaxPayload - 256 * 1024) * 0.74);
    public const int MaxFilesPerClip = 100; // sender-side cap; receiver stays lenient

    private readonly IWin32Clipboard _clipboard;
    private readonly string _receivedDir;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private string? _lastText;
    private string? _lastImageHash;
    // Always-expired sentinel: the Stopwatch's epoch is type init (~0),
    // unlike the boot-based monotonic clocks in the Python/Swift ports,
    // so 0.0 would swallow the first image copied within 1 s of startup.
    private double _lastImageSendAt = double.NegativeInfinity;
    private List<(string Path, long Size, long MTimeTicks)> _lastFileFingerprints = new();
    private int _consecReadFails;
    private bool _readFailWarned;
    private bool _updateRunning;
    private bool _rerunRequested;

    public Func<ClipPayload, Task>? OnLocalChange { get; set; }
    public Func<string, Task>? OnFileSkipped { get; set; }

    public ClipboardWatcher(IWin32Clipboard clipboard, string receivedDir)
    {
        _clipboard = clipboard;
        _receivedDir = receivedDir;
        // Seed baselines so startup clipboard content never fires a send.
        // SafeRead: a clipboard held by another process at startup must not
        // crash Program.
        _lastText = SafeRead(clipboard.GetText);
        if (SafeRead(clipboard.GetImagePng) is { } png) _lastImageHash = Hashing.Sha256Hex(png);
        if (SafeRead(clipboard.GetFilePaths) is { } paths)
            _lastFileFingerprints = FingerprintList(paths);
    }

    /// The daemon's pump: events arrive from the UI message loop, so this
    /// just parks until cancelled.
    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);

    /// Called (on the UI thread) for every WM_CLIPBOARDUPDATE. Passes are
    /// strictly serial like the references' sequential poll loop: Windows
    /// delivers multiple WM_CLIPBOARDUPDATE per logical copy, and
    /// overlapping passes could dispatch the same file twice. An update
    /// arriving mid-pass coalesces into one rerun (event-driven code has
    /// no "next poll" to catch a missed change). Plain fields suffice —
    /// every invocation arrives on the single UI thread.
    public async Task HandleClipboardUpdateAsync()
    {
        if (_updateRunning) { _rerunRequested = true; return; }
        _updateRunning = true;
        try
        {
            do
            {
                _rerunRequested = false;
                await RunUpdatePassAsync();
            }
            while (_rerunRequested);
        }
        finally { _updateRunning = false; }
    }

    /// Soft-failure clipboard read, mirroring Python's _safe_paste
    /// (anyclip.py:854-871): Windows reads routinely fail transiently
    /// (CLIPBRD_E_CANT_OPEN while another process holds the clipboard).
    /// Counts consecutive failures, warns once at ReadFailWarnAt, resets
    /// on success.
    private T? SafeRead<T>(Func<T?> read) where T : class
    {
        try
        {
            var result = read();
            _consecReadFails = 0;
            _readFailWarned = false;
            return result;
        }
        catch (Exception e)
        {
            int n = ++_consecReadFails;
            RotatingLog.Shared.Debug($"clipboard read failed (#{n}): {e.Message}");
            if (n >= ReadFailWarnAt && !_readFailWarned)
            {
                _readFailWarned = true;
                RotatingLog.Shared.Warning(
                    $"clipboard read failing: {n} consecutive errors "
                    + "(check clipboard access / another process may be "
                    + "holding the clipboard)");
            }
            return null;
        }
    }

    /// One full pass over text/image/file. Every dispatch is exception-
    /// isolated (anyclip.py:885-888/912-915/973-976): a failing handler
    /// never aborts the remaining kind checks.
    private async Task RunUpdatePassAsync()
    {
        var text = SafeRead(_clipboard.GetText);
        if (text is not null && text != _lastText)
        {
            _lastText = text;
            if (text.Length > 0)
            {
                try { await (OnLocalChange?.Invoke(new TextClip(text)) ?? Task.CompletedTask); }
                catch (Exception e)
                { RotatingLog.Shared.Error($"on_change(text) handler failed: {e}"); }
            }
            else
                RotatingLog.Shared.Debug("clipboard cleared (empty text); not propagating");
        }

        if (SafeRead(_clipboard.GetImagePng) is { } png)
        {
            var hash = Hashing.Sha256Hex(png);
            if (hash != _lastImageHash)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now - _lastImageSendAt < ImageCooldownSeconds)
                {
                    _lastImageHash = hash;
                    RotatingLog.Shared.Debug("image change within cooldown, dropping");
                }
                else
                {
                    _lastImageHash = hash;
                    _lastImageSendAt = now;
                    try { await (OnLocalChange?.Invoke(new ImageClip(png)) ?? Task.CompletedTask); }
                    catch (Exception e)
                    { RotatingLog.Shared.Error($"on_change(image) handler failed: {e}"); }
                }
            }
        }

        await CheckFileClipboardAsync();
    }

    private static (string Path, long Size, long MTimeTicks)? Fingerprint(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                var di = new DirectoryInfo(path);
                if (!di.Exists) return null;
                return (path, -1, di.LastWriteTimeUtc.Ticks); // folders: size -1
            }
            return (path, info.Length, info.LastWriteTimeUtc.Ticks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return null; }
    }

    private static List<(string Path, long Size, long MTimeTicks)> FingerprintList(
        IReadOnlyList<string> paths)
    {
        var list = new List<(string Path, long Size, long MTimeTicks)>(paths.Count);
        foreach (var p in paths)
            if (Fingerprint(p) is { } fp) list.Add(fp);
        return list;
    }

    private async Task CheckFileClipboardAsync()
    {
        var paths = SafeRead(_clipboard.GetFilePaths);
        if (paths is null || paths.Count == 0) return;
        var fps = FingerprintList(paths);
        if (fps.Count == 0 || fps.SequenceEqual(_lastFileFingerprints)) return;
        // Record FIRST — the fingerprint is always taken (even if everything is
        // skipped) so nothing retry-loops.
        _lastFileFingerprints = fps;

        // Split folders from files. Skipped folders are collected here and named
        // in ONE notification afterwards (spec: one skip notification per
        // detection pass). Mirrors the Python `skipped_folders` accumulation.
        var files = new List<string>();
        var skippedFolders = new List<string>();
        foreach (var p in paths)
        {
            if (Directory.Exists(p))
            {
                var display = Path.GetFileName(p.TrimEnd('/', '\\'));
                if (string.IsNullOrEmpty(display)) display = p; // drive roots
                RotatingLog.Shared.Warning($"folder on clipboard not synced (unsupported): {p}");
                skippedFolders.Add(display);
            }
            else files.Add(p);
        }
        if (skippedFolders.Count == 1)
            await SafeSkipAsync(
                $"folder not synced — folders are not supported: {skippedFolders[0]}");
        else if (skippedFolders.Count >= 2)
            await SafeSkipAsync(
                $"{skippedFolders.Count} folders not synced — folders are not supported");

        // Greedy: keep files in selection order while sum(raw) <= budget and
        // count <= cap. Skipped (too large, over-cap, or unreadable) -> one toast.
        var sendable = new List<(string Name, byte[] Data)>();
        long cumulative = 0;
        int skipped = 0;
        foreach (var path in files)
        {
            if (sendable.Count >= MaxFilesPerClip) { skipped++; continue; }
            long size;
            try { size = new FileInfo(path).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning($"file stat failed for {path}: {e.Message}; skipping");
                skipped++; continue;
            }
            if (cumulative + size > FileBudget) { skipped++; continue; }
            byte[] data;
            try { data = await File.ReadAllBytesAsync(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                RotatingLog.Shared.Warning($"file read failed for {path}: {e.Message}; skipping");
                skipped++; continue;
            }
            cumulative += size;
            sendable.Add((Path.GetFileName(path), data));
        }
        if (skipped > 0)
            await SafeSkipAsync($"{skipped} file(s) skipped (too large to sync)");

        if (sendable.Count == 0) return;
        ClipPayload payload = sendable.Count == 1
            ? new FileClip(sendable[0].Name, sendable[0].Data)
            : new FilesClip(sendable);
        try { await (OnLocalChange?.Invoke(payload) ?? Task.CompletedTask); }
        catch (Exception e)
        { RotatingLog.Shared.Error($"on_change(files) handler failed: {e}"); }
    }

    private async Task SafeSkipAsync(string message)
    {
        try { await (OnFileSkipped?.Invoke(message) ?? Task.CompletedTask); }
        catch (Exception e)
        { RotatingLog.Shared.Error($"on_file_skipped handler failed: {e}"); }
    }

    /// Inbound (peer → local). Baselines updated BEFORE writes.
    public Task<bool> ApplyRemoteAsync(ClipPayload payload)
    {
        switch (payload)
        {
            case TextClip t:
                _lastText = t.Text;
                return Task.FromResult(_clipboard.SetText(t.Text));
            case ImageClip i:
                _lastImageHash = Hashing.Sha256Hex(i.Png);
                bool ok = _clipboard.SetImagePng(i.Png);
                if (!ok) RotatingLog.Shared.Warning("clipboard write (image) failed");
                return Task.FromResult(ok);
            case FileClip f:
                try
                {
                    Directory.CreateDirectory(_receivedDir);
                    string target = Path.Combine(_receivedDir, TextHelpers.SanitizeFilename(f.Name));
                    File.WriteAllBytes(target, f.Data);
                    _lastFileFingerprints = FingerprintList(new[] { target });
                    bool fileOk = _clipboard.SetFilePaths(new[] { target });
                    if (!fileOk) RotatingLog.Shared.Warning("clipboard write (file) failed");
                    return Task.FromResult(fileOk);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"file write to {_receivedDir} failed: {e.Message}");
                    return Task.FromResult(false);
                }
            case FilesClip fs:
                try
                {
                    Directory.CreateDirectory(_receivedDir);
                    var sanitized = TextHelpers.UniquifyNames(
                        fs.Files.Select(x => TextHelpers.SanitizeFilename(x.Name)).ToList());
                    var placed = new List<string>(fs.Files.Count);
                    for (int i = 0; i < fs.Files.Count; i++)
                    {
                        string target = Path.Combine(_receivedDir, sanitized[i]);
                        File.WriteAllBytes(target, fs.Files[i].Data);
                        placed.Add(target);
                    }
                    // Baseline to the paths actually PLACED on the clipboard.
                    _lastFileFingerprints = FingerprintList(placed);
                    bool filesOk = _clipboard.SetFilePaths(placed);
                    if (!filesOk) RotatingLog.Shared.Warning("clipboard write (files) failed");
                    return Task.FromResult(filesOk);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"files write to {_receivedDir} failed: {e.Message}");
                    return Task.FromResult(false);
                }
            default:
                return Task.FromResult(false);
        }
    }
}

/// True message-only window (parent HWND_MESSAGE — the documented target
/// for AddClipboardFormatListener, and it still receives
/// WM_CLIPBOARDUPDATE); created on the UI thread by Program and
/// forwarding to the watcher.
public sealed class ClipboardListenerWindow : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private readonly Func<Task> _onUpdate;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    public ClipboardListenerWindow(Func<Task> onUpdate)
    {
        _onUpdate = onUpdate;
        CreateHandle(new CreateParams { Parent = (IntPtr)(-3) /* HWND_MESSAGE */ });
        AddClipboardFormatListener(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
            _ = HandleSafelyAsync(); // fire-and-forget WITH a logging backstop
        base.WndProc(ref m);
    }

    /// Backstop so no clipboard-path exception is ever silently discarded
    /// through the discarded task (the watcher already isolates per-kind
    /// handler errors; this catches anything that still escapes).
    private async Task HandleSafelyAsync()
    {
        try { await _onUpdate(); }
        catch (Exception e)
        { RotatingLog.Shared.Error($"clipboard update handler failed: {e}"); }
    }

    public void Dispose()
    {
        RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }
}
