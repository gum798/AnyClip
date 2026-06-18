using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

internal sealed class FakeClipboard : IClipboardSync
{
    public Func<ClipPayload, Task>? OnLocalChange { get; set; }
    public Func<string, Task>? OnFileSkipped { get; set; }
    public List<ClipPayload> Applied { get; } = new();
    public Task RunAsync(CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    public Task<bool> ApplyRemoteAsync(ClipPayload payload)
    {
        lock (Applied) Applied.Add(payload);
        return Task.FromResult(true);
    }
}

/// Clipboard whose RunAsync returns immediately (RanToCompletion) — stands in
/// for a daemon background task that ends unexpectedly while the app was not
/// asked to quit (the Windows sleep/resume case where the listener accept loop
/// returned normally and the supervisor silently exited).
internal sealed class CompletingClipboard : IClipboardSync
{
    public Func<ClipPayload, Task>? OnLocalChange { get; set; }
    public Func<string, Task>? OnFileSkipped { get; set; }
    public int RunCalls;
    public Task RunAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref RunCalls);
        return Task.CompletedTask;
    }
    public Task<bool> ApplyRemoteAsync(ClipPayload payload) => Task.FromResult(true);
}

internal sealed class FakeMdns : IMdnsService
{
    public string? AdvertisedIp => "127.0.0.1";
    public bool Started; public bool Stopped; public int Refreshes;
    public Task StartAsync(string instanceName, int port, IReadOnlyList<(string, string)> txt)
    { Started = true; return Task.CompletedTask; }
    public void Refresh() => Refreshes++;
    public void Stop() => Stopped = true;
}

internal sealed class FakePidLock : IPidLock
{
    public bool Prepared; public bool Released;
    public void Prepare(int port) => Prepared = true;
    public void Release() => Released = true;
}

public class DaemonTests
{
    [Fact]
    public void SyncCoordinatorSuppressesEcho() // sync body: async Task would be CS1998
    {
        var c = new SyncCoordinator();
        c.MarkReceived("text", "h1");
        Assert.False(c.ShouldSend("text", "h1"));
        Assert.True(c.ShouldSend("text", "h2"));
        Assert.True(c.ShouldSend("image", "h1"));
    }

    [Fact]
    public void ClearDirectoryFilesKeepsSubdirs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyclip-clear-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(dir, "sub"));
        File.WriteAllText(Path.Combine(dir, "a.txt"), "x");
        Daemon.ClearDirectoryFiles(dir);
        Assert.Equal(new[] { "sub" },
            Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task DaemonStartsServesAndShutsDownCleanly()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-daemon-" + Guid.NewGuid());
        var pid = new FakePidLock();
        var mdns = new FakeMdns();
        var clip = new FakeClipboard();
        var daemon = new Daemon(
            new DaemonConfig("test-token", 28621, "daemon-test", NotificationsEnabled: false),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: mdns, pidLock: pid,
            primaryIPv4: () => "127.0.0.1",
            notify: (_, _) => { }, onFatal: _ => { });

        using var cts = new CancellationTokenSource();
        var run = daemon.RunForeverAsync(cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline && !(pid.Prepared && mdns.Started))
            await Task.Delay(50);
        Assert.True(pid.Prepared);
        Assert.True(mdns.Started);

        cts.Cancel();
        await run; // RunForeverAsync swallows cancellation and returns
        Assert.True(pid.Released);
        Assert.True(mdns.Stopped);
    }

    [Fact]
    public async Task DaemonRestartsWhenABackgroundTaskExitsUnexpectedly()
    {
        // Regression: a background task that completes RanToCompletion (here the
        // clipboard loop) while the app was NOT asked to quit must bounce the
        // daemon, not silently exit the supervisor with a dead listener. Field
        // bug: on Windows sleep/resume the listener accept loop returned
        // normally and the IsFaulted-only supervisor treated it as a clean
        // shutdown, wedging the daemon (tray up, tcp/24816 dead) for ~18 min
        // until a manual relaunch.
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-restart-" + Guid.NewGuid());
        var clip = new CompletingClipboard();
        var daemon = new Daemon(
            new DaemonConfig("test-token", 28623, "daemon-test", NotificationsEnabled: false),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
            primaryIPv4: () => "127.0.0.1",
            notify: (_, _) => { }, onFatal: _ => { });

        using var cts = new CancellationTokenSource();
        var run = daemon.RunForeverAsync(cts.Token);
        // Each RunOnceAsync calls clipboard.RunAsync once; a restart calls it
        // again. Without the fix RunCalls sticks at 1 (supervisor exits).
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && Volatile.Read(ref clip.RunCalls) < 2)
            await Task.Delay(50);
        Assert.True(Volatile.Read(ref clip.RunCalls) >= 2,
            $"daemon should restart after a task exits unexpectedly; RunAsync calls={clip.RunCalls}");

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task FatalStopsSupervisorAndCallsOnFatal()
    {
        string? fatal = null;
        var pid = new ThrowingPidLock();
        var daemon = new Daemon(
            new DaemonConfig("t", 28622, "x", NotificationsEnabled: false),
            "0.0.0-test", Path.GetTempPath(),
            new FakeClipboard(), new FakeMdns(), pid,
            () => null, (_, _) => { }, m => fatal = m);
        await daemon.RunForeverAsync(CancellationToken.None);
        Assert.NotNull(fatal);
    }

    private sealed class ThrowingPidLock : IPidLock
    {
        public void Prepare(int port) => throw new FatalStartupException("boom");
        public void Release() { }
    }
}
