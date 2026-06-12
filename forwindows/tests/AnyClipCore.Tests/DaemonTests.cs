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
