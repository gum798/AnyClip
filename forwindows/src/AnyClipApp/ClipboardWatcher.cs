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

    public bool SetText(string text) => TrySet("text", () =>
    {
        Clipboard.SetText(text);
        // Read-back: a DLP/security agent that clears or rewrites the
        // clipboard right after a successful write would otherwise be
        // indistinguishable from a good sync.
        if (Clipboard.GetText() != text)
            throw new InvalidOperationException(
                "clipboard cleared or altered immediately after write");
    });

    public bool SetImagePng(byte[] png) => TrySet("image", () =>
    {
        using var ms = new MemoryStream(png);
        using var image = Image.FromStream(ms);
        Clipboard.SetImage(image);
    });

    public bool SetFilePaths(IReadOnlyList<string> paths) => TrySet("files", () =>
    {
        var sc = new System.Collections.Specialized.StringCollection();
        foreach (var p in paths) sc.Add(p);
        Clipboard.SetFileDropList(sc);
    });

    /// WinForms Set* already retries CLIPBRD_E_CANT_OPEN internally
    /// (10 x 100 ms) before throwing; one further beat outlasts agents that
    /// hold the clipboard just over a second. Failures were swallowed
    /// unlogged through 1.4.2 — a locked clipboard looked exactly like a
    /// successful sync — so name the failure and, when possible, the process
    /// holding the clipboard open. Each attempt is its own STA hop and the
    /// backoff runs on the calling (daemon) thread: sleeping inside the
    /// Invoke would stall the message pump while this process may be the
    /// clipboard owner with unrendered formats, hanging anyone who pastes.
    private bool TrySet(string kind, Action write)
    {
        for (int attempt = 1; ; attempt++)
        {
            Exception? failure = null;
            bool ok = OnSta(() =>
            {
                try { write(); return true; }
                catch (Exception e) { failure = e; return false; }
            });
            if (ok) return true;
            RotatingLog.Shared.Warning(ClipboardDiagnostics.DescribeWriteFailure(
                kind, failure!, OpenClipboardOwnerProcess()) + $" (attempt {attempt}/2)");
            if (attempt >= 2 || !ClipboardDiagnostics.IsRetryable(failure!)) return false;
            Thread.Sleep(300);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetOpenClipboardWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    /// Name the process that has the clipboard open right now, if any —
    /// immediately after a failed write this is usually the culprit.
    private static string? OpenClipboardOwnerProcess()
    {
        try
        {
            var h = GetOpenClipboardWindow();
            if (h == IntPtr.Zero) return null;
            _ = GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            return $"{p.ProcessName} (pid {pid})";
        }
        catch { return null; }
    }
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
    /// Sender-side cap; receiver stays lenient. Raised 100 -> 500 for protocol
    /// 1.3: a document tree passes 100 files easily and FileBudget is the real
    /// limit (worst-case extra JSON envelope still fits the 256 KiB reservation).
    public const int MaxFilesPerClip = 500;

    private readonly IWin32Clipboard _clipboard;
    private readonly string _receivedDir;
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private string? _lastText;
    private string? _lastImageHash;
    // Always-expired sentinel: the Stopwatch's epoch is type init (~0),
    // unlike the boot-based monotonic clocks in the Python/Swift ports,
    // so 0.0 would swallow the first image copied within 1 s of startup.
    private double _lastImageSendAt = double.NegativeInfinity;
    private IReadOnlyList<(string Path, long Size, long MTimeTicks)> _lastFileFingerprints =
        Array.Empty<(string, long, long)>();
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

    /// The fingerprint half of the scan, for the two baselines (startup and
    /// inbound placement) that have no use for the expansion. Both baselines and
    /// the change comparison go through the SAME scan, so the two sides always
    /// see the same shape — including one triple per file inside a copied or
    /// just-placed folder. Port of anyclip.fingerprint_paths.
    private static IReadOnlyList<(string Path, long Size, long MTimeTicks)> FingerprintList(
        IReadOnlyList<string> paths) =>
        FolderExpander.ScanSelection(paths, FileBudget, MaxFilesPerClip).Fingerprints;

    private async Task CheckFileClipboardAsync()
    {
        var paths = SafeRead(_clipboard.GetFilePaths);
        if (paths is null || paths.Count == 0) return;
        // ONE stat + walk pass per clipboard change, on a BACKGROUND thread.
        // The walk does run on every change — noticing an edit deep inside a
        // tree requires that — but FolderExpander bails out at the absolute
        // caps, so its cost is bounded, and a 500-file tree walk never runs on
        // the UI thread that WM_CLIPBOARDUPDATE arrives on. What the comparison
        // below saves is the re-SEND and the re-READ of an unchanged selection,
        // not the walk. Mirrors asyncio.to_thread(scan_selection, paths) and
        // Swift's off-main-actor scan.
        var scan = await Task.Run(
            () => FolderExpander.ScanSelection(paths, FileBudget, MaxFilesPerClip));
        var fps = scan.Fingerprints;
        if (fps.Count == 0 || fps.SequenceEqual(_lastFileFingerprints)) return;

        // Folders are EXPANDED, not skipped (protocol 1.3): each becomes a set
        // of entries carrying a "path" relative to the copied folder. This
        // REPLACES the old "folder on clipboard not synced (unsupported)" path.
        // Per-folder all-or-nothing against the remaining budget/count; loose
        // files keep the greedy per-file rule and its existing toast. The reads
        // are async, so the UI thread is never blocked on file I/O either.
        var plan = await FolderExpander.ExpandAsync(scan.Items, FileBudget, MaxFilesPerClip);

        // Record fingerprint only when there are no transient unreadable / locked files.
        // If a file failed due to temporary lock contention, keeping the previous
        // baseline allows a subsequent Ctrl+C re-copy to retry immediately once the
        // file is released or closed. Deterministic skips (oversized folder, empty
        // folder) have UnreadableFiles empty, so they are baselined and do not retry-loop.
        if (plan.UnreadableFiles.Count == 0)
            _lastFileFingerprints = fps;

        // Both strings are constraints-pinned and built in Core, so the
        // platform-neutral suite pins the wording even though these dispatch
        // sites only RUN on Windows.
        foreach (var name in plan.TooLargeFolders)
            await SafeSkipAsync(FolderExpander.TooLargeToastMessage(name));
        // One toast however many folders came back empty — the wording names none.
        if (plan.EmptyFolders.Count > 0)
            await SafeSkipAsync(FolderExpander.EmptyToastMessage());
        if (plan.SkippedFiles > 0)
            await SafeSkipAsync($"{plan.SkippedFiles} file(s) skipped (too large to sync)");
        if (plan.UnreadableFiles.Count == 1)
            await SafeSkipAsync(FolderExpander.UnreadableToastMessage(
                Path.GetFileName(plan.UnreadableFiles[0])));
        else if (plan.UnreadableFiles.Count > 1)
            await SafeSkipAsync(FolderExpander.UnreadableCountToastMessage(plan.UnreadableFiles.Count));

        if (plan.Entries.Count == 0) return;
        // A single LOOSE file keeps the legacy kind:"file" frame; a single
        // folder-derived file must stay kind:"files" or its path is lost.
        ClipPayload payload = plan.Entries.Count == 1 && plan.Entries[0].RelPath is null
            ? new FileClip(plan.Entries[0].Name, plan.Entries[0].Data)
            : new FilesClip(plan.Entries);
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
    public async Task<bool> ApplyRemoteAsync(ClipPayload payload)
    {
        // kind:"files" has its own entry point (the daemon needs the placed
        // shape); routed through it here so any other caller — and the App
        // suite — still gets a plain answer. For a files clip that answer is
        // "did anything land under received/": a failed CF_HDROP write is
        // logged rather than returned, because the peer's files ARE on disk.
        if (payload is FilesClip files)
            return (await ApplyFilesAsync(files)).TopLevelItems.Count > 0;
        return await ApplyOtherAsync(payload);
    }

    /// Rebuild one inbound files clip under received/ — folder entries into
    /// their tree, everything else flat — then place the TOP-LEVEL items (each
    /// rebuilt folder once, plus every loose file) on the clipboard in ONE
    /// CF_HDROP write, in batch order.
    ///
    /// Tree rebuild, flat fallback, top-folder uniquify and the traversal
    /// guards all live in ReceivedTree (Core, platform-neutral tests); this
    /// layer only puts the result on the clipboard. Returns what actually
    /// landed so the daemon can word the toast and seed echo suppression from
    /// the PLACED shape. Port of Swift ClipboardWatcher.updateLocalFiles.
    public async Task<ReceivedTree.PlacedFiles> ApplyFilesAsync(FilesClip clip)
    {
        try
        {
            // Every byte of disk work — up to 500 files, ~49 MB of writes, and
            // the walk that fingerprints the placed tree — runs on a background
            // thread; only the clipboard hand-off below needs the caller's.
            // Mirrors Swift's offMainActor writeInbound and Python's
            // asyncio.to_thread(watcher.update_local_files, ...).
            var (placed, fingerprints) = await Task.Run(() =>
            {
                var written = ReceivedTree.Write(_receivedDir, clip.Files);
                return (written, FingerprintList(written.TopPaths));
            });
            if (placed.TopPaths.Count == 0) return ReceivedTree.PlacedFiles.Empty;
            // Baseline to exactly the paths going on the clipboard (folder
            // expansion included) BEFORE the write, so the placement cannot
            // echo back out. No await between the two.
            _lastFileFingerprints = fingerprints;
            // A failed CF_HDROP write is logged (in detail, by the clipboard
            // layer), not returned. `placed` is reported even then: the bytes
            // ARE under received/, so the toast that names the folder is still
            // true and still useful. Matching Swift — zeroing here would toast
            // "0 files" for a clip that landed.
            _clipboard.SetFilePaths(placed.TopPaths);
            return placed;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            RotatingLog.Shared.Warning($"files write to {_receivedDir} failed: {e.Message}");
            return ReceivedTree.PlacedFiles.Empty;
        }
    }

    private Task<bool> ApplyOtherAsync(ClipPayload payload)
    {
        switch (payload)
        {
            case TextClip t:
                _lastText = t.Text;
                return Task.FromResult(_clipboard.SetText(t.Text));
            case ImageClip i:
                _lastImageHash = Hashing.Sha256Hex(i.Png);
                return Task.FromResult(_clipboard.SetImagePng(i.Png));
            case FileClip f:
                try
                {
                    Directory.CreateDirectory(_receivedDir);
                    string target = Path.Combine(_receivedDir, TextHelpers.SanitizeFilename(f.Name));
                    File.WriteAllBytes(target, f.Data);
                    _lastFileFingerprints = FingerprintList(new[] { target });
                    return Task.FromResult(_clipboard.SetFilePaths(new[] { target }));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    RotatingLog.Shared.Warning($"file write to {_receivedDir} failed: {e.Message}");
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
