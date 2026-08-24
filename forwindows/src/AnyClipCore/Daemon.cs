using System.Threading.Channels;

namespace AnyClip.Core;

/// Platform surface injected into the daemon so the assembly is testable
/// off-Windows. The WinForms layer provides the real implementations.
public interface IClipboardSync
{
    Func<ClipPayload, Task>? OnLocalChange { get; set; }
    Func<string, Task>? OnFileSkipped { get; set; }
    /// Long-running pump (or infinite wait when events come from the UI loop).
    Task RunAsync(CancellationToken ct);
    /// Write a remote payload to the local clipboard; false = write failed.
    Task<bool> ApplyRemoteAsync(ClipPayload payload);
}

public interface IPidLock
{
    void Prepare(int port); // throws FatalStartupException on foreign conflicts
    void Release();
}

public sealed record DaemonConfig(
    string Token,
    int Port,
    string Name,
    bool NotificationsEnabled = true);

/// Echo-suppression shared by inbound and outbound paths. Lock-based.
public sealed class SyncCoordinator
{
    private readonly object _lock = new();
    private readonly EchoSuppressor _suppressor = new();
    public void MarkReceived(string kind, string hash)
    { lock (_lock) _suppressor.MarkReceived(kind, hash); }
    public bool ShouldSend(string kind, string hash)
    { lock (_lock) return _suppressor.ShouldSend(kind, hash); }
}

/// Assembles one daemon runtime and supervises it with 1→60 s backoff.
/// Port of formacOS Daemon.swift / anyclip.run()+main().
public sealed class Daemon(
    DaemonConfig config,
    string appVersion,
    string stateDir,
    IClipboardSync clipboard,
    IMdnsService mdns,
    IPidLock pidLock,
    Func<string?> primaryIPv4,
    Action<string, string> notify,
    Action<string> onFatal)
{
    private readonly Channel<DaemonEvent> _events =
        Channel.CreateUnbounded<DaemonEvent>();
    public ChannelReader<DaemonEvent> Events => _events.Reader;

    /// One aggregated toast for the peers a clip was too large for — their
    /// protocol is &lt; 1.2, so they still enforce the legacy 16 MiB receive cap
    /// and the fan-out skipped them (their links stayed up). null when nothing
    /// was skipped; at most ONE per clip. Keep in lockstep with
    /// anyclip.size_skip_message / Swift sizeSkipMessage.
    public static string? SizeSkipMessage(IReadOnlyList<string> names)
    {
        if (names.Count == 0) return null;
        if (names.Count == 1)
            return $"clip not sent to {names[0]} (too large for its AnyClip version)";
        return $"clip not sent to {names.Count} peer(s) (too large for their AnyClip version)";
    }

    public static void ClearDirectoryFiles(string dir)
    {
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir))
            try { File.Delete(f); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { RotatingLog.Shared.Debug($"could not remove {f}: {e.Message}"); }
    }

    public async Task RunForeverAsync(CancellationToken ct)
    {
        double backoff = 1;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(ct);
                return;
            }
            catch (OperationCanceledException) { return; }
            catch (FatalStartupException e)
            {
                RotatingLog.Shared.Error($"fatal: {e.Message}");
                onFatal(e.Message);
                return;
            }
            catch (Exception e)
            {
                if (ct.IsCancellationRequested) return;
                RotatingLog.Shared.Error(
                    $"daemon crashed: {e.Message}; restarting in {(int)backoff}s");
                try { await Task.Delay(TimeSpan.FromSeconds(backoff), ct); }
                catch (OperationCanceledException) { return; }
                backoff = Math.Min(backoff * 2, 60);
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken outerCt)
    {
        pidLock.Prepare(config.Port);
        string receivedDir = Path.Combine(stateDir, "received");
        ClearDirectoryFiles(receivedDir);

        string nodeId = Guid.NewGuid().ToString().ToLowerInvariant();
        var coordinator = new SyncCoordinator();
        Action<DaemonEvent> emit = e => _events.Writer.TryWrite(e);
        Action<string, string> toast = config.NotificationsEnabled
            ? notify : (_, _) => { };

        var manager = new LinkManager(
            new LinkConfig(config.Token, config.Port, config.Name, appVersion), nodeId);
        manager.Emit = emit;
        // Received applies arrive already serialized through the manager's single
        // apply queue; mark the (global) suppressor BEFORE touching the clipboard.
        manager.OnClip = async (payload, peer) =>
        {
            coordinator.MarkReceived(payload.Kind, payload.PayloadHash);
            bool ok = await clipboard.ApplyRemoteAsync(payload);
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"<- received text {t.Text.Length} chars from {peer}");
                    toast($"AnyClip ← {peer}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info(
                        $"<- received image {i.Png.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info(
                        $"<- received file {f.Name} {f.Data.Length} bytes from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    if (fsc.Files.Count == 1)
                        coordinator.MarkReceived("file", Hashing.Sha256Hex(fsc.Files[0].Data));
                    RotatingLog.Shared.Info(
                        $"<- received {fsc.Files.Count} files from {peer} "
                        + $"({(ok ? "written to clipboard" : "WRITE FAILED")})");
                    toast($"AnyClip ← {peer}", $"{fsc.Files.Count} files");
                    break;
            }
        };

        clipboard.OnLocalChange = async payload =>
        {
            if (manager.ActiveLinkCount == 0) return;
            if (!coordinator.ShouldSend(payload.Kind, payload.PayloadHash))
            {
                RotatingLog.Shared.Debug($"skip echo of just-received {payload.Kind}");
                return;
            }
            // Fan out to all links; per-link minor gating (files vs first-file
            // fallback) happens inside BroadcastAsync. OldPeerDrops is aggregated
            // so at most ONE skip toast fires for this local copy across all peers.
            var result = await manager.BroadcastAsync(payload);
            // Same aggregation for the peers the legacy 16 MiB size gate skipped:
            // ONE toast per local copy, never one per peer. Emitted BEFORE the
            // nothing-sent bail-out — when every link is pre-1.2 the clip reaches
            // no one, and that is exactly when the user needs to be told.
            if (SizeSkipMessage(result.SizeSkipped) is { } sizeSkipMessage)
                _ = clipboard.OnFileSkipped?.Invoke(sizeSkipMessage);
            if (result.Sent == 0) return;
            if (result.OldPeerDrops > 0)
                _ = clipboard.OnFileSkipped?.Invoke(
                    $"{result.OldPeerDrops} file(s) not synced — update the peer's AnyClip for multi-file sync");
            string peers = string.Join(", ", manager.LinkedPeerNames);
            if (string.IsNullOrEmpty(peers)) peers = "peer";
            switch (payload)
            {
                case TextClip t:
                    RotatingLog.Shared.Info($"-> sent text {t.Text.Length} chars to {peers}");
                    toast($"AnyClip → {peers}", TextHelpers.Preview(t.Text));
                    break;
                case ImageClip i:
                    RotatingLog.Shared.Info($"-> sent image {i.Png.Length} bytes to {peers}");
                    toast($"AnyClip → {peers}", $"image ({i.Png.Length / 1024} KB)");
                    break;
                case FileClip f:
                    RotatingLog.Shared.Info($"-> sent file {f.Name} {f.Data.Length} bytes to {peers}");
                    toast($"AnyClip → {peers}", $"file: {f.Name} ({f.Data.Length / 1024} KB)");
                    break;
                case FilesClip fsc:
                    RotatingLog.Shared.Info($"-> sent {fsc.Files.Count} files to {peers}");
                    toast($"AnyClip → {peers}", $"{fsc.Files.Count} files");
                    break;
            }
        };
        clipboard.OnFileSkipped = msg => { toast("AnyClip", msg); return Task.CompletedTask; };

        var directory = new PeerDirectory(nodeId, emit,
            (host, port, label) => manager.TryConnectAsync(host, port, label, outerCt));
        // The App's MdnsBeacon needs the directory to ingest into; expose it.
        CurrentDirectory = directory;

        await mdns.StartAsync(
            $"{config.Name}-{nodeId[..8]}",
            config.Port,
            new[]
            {
                ("id", nodeId),
                ("version", Wire.LegacyVersion.ToString()),
                ("app_version", appVersion),
                ("protocol_major", Wire.ProtocolMajor.ToString()),
                ("protocol_minor", Wire.ProtocolMinor.ToString()),
            });
        RotatingLog.Shared.Info(
            $"AnyClip starting (node {nodeId[..8]}, name={config.Name})");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var tasks = new[]
        {
            manager.ServeAsync(cts.Token),
            clipboard.RunAsync(cts.Token),
            Watchdogs.MdnsReconnectLoopAsync(directory, manager, cts.Token),
            Watchdogs.NetworkWatchdogAsync(mdns, primaryIPv4, 15, cts.Token),
            Watchdogs.IdleLinkWatchdogAsync(mdns, manager, 60, 3, cts.Token),
        };
        try
        {
            // asyncio.gather semantics: first task to settle wins, siblings drained.
            var first = await Task.WhenAny(tasks);
            cts.Cancel();
            try { await Task.WhenAll(tasks); } catch { /* drained below */ }
            if (first.IsFaulted)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo
                    .Capture(first.Exception!.InnerException ?? first.Exception!).Throw();
            // A background task that settled WITHOUT the app asking to quit means
            // the daemon lost a leg — e.g. on a Windows sleep/resume the OS aborts
            // the listen socket and ServeAsync's accept loop returns RanToCompletion
            // (not faulted). Treat any such non-quit completion as a restart
            // trigger instead of letting RunOnceAsync return normally, which would
            // silently exit the supervisor with tcp/24816 unbound (the field wedge:
            // tray alive, listener dead, no recovery until manual relaunch). macOS/
            // Python can't hit this because their serve() never returns normally.
            if (!outerCt.IsCancellationRequested)
                throw new DaemonRestartException(
                    $"daemon task exited unexpectedly (status={first.Status}); bouncing daemon");
            outerCt.ThrowIfCancellationRequested();
        }
        finally
        {
            manager.Shutdown();
            mdns.Stop();
            pidLock.Release();
            ClearDirectoryFiles(receivedDir);
            CurrentDirectory = null;
        }
    }

    /// The live PeerDirectory of the current runOnce (null between runs).
    /// The Windows MdnsBeacon ingests browse results into it.
    public PeerDirectory? CurrentDirectory { get; private set; }
}
