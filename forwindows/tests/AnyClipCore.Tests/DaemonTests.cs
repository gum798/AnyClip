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
    public void SyncCoordinatorSuppressesFilesAggregateAndSingleFile()
    {
        var c = new SyncCoordinator();
        var agg = Hashing.AggregateFilesHash(new[]
        {
            Hashing.Sha256Hex("one"u8.ToArray()),
            Hashing.Sha256Hex("two"u8.ToArray()),
        });
        c.MarkReceived("files", agg);
        Assert.False(c.ShouldSend("files", agg));        // echo of just-received set suppressed
        Assert.True(c.ShouldSend("files", "other-agg"));  // a different set still sends
        var h = Hashing.Sha256Hex("solo"u8.ToArray());
        c.MarkReceived("file", h);                        // 1-placed receive rule also marks "file"
        Assert.False(c.ShouldSend("file", h));
    }

    private static async Task<FramedConnection> ConnectWithRetry(int port, CancellationToken ct)
    {
        for (int i = 0; ; i++)
        {
            try { return await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct); }
            catch when (i < 40) { await Task.Delay(100, ct); }
        }
    }

    [Fact]
    public async Task OldPeerDowngradesFilesClipToFirstFileWithNotification()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-downgrade-" + Guid.NewGuid());
        var clip = new FakeClipboard();
        var notes = new List<string>();
        var daemon = new Daemon(
            new DaemonConfig("dg-token", 28625, "dg", NotificationsEnabled: true),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
            primaryIPv4: () => "127.0.0.1",
            notify: (_, body) => { lock (notes) notes.Add(body); }, onFatal: _ => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = daemon.RunForeverAsync(cts.Token);

        using var raw = await ConnectWithRetry(28625, cts.Token);
        var oldHello = WireMessage.Hello(
            Hashing.Sha256Hex("dg-token"), "ffffffff-old", "old-peer", "1.0.0")
            with { ProtocolMinor = 0 };
        await raw.SendFrameAsync(oldHello, cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token); // daemon hello

        // Wait for the link to register (guarantees link.IsActive before we fire).
        async Task<bool> WaitForLinkUp(double seconds = 10)
        {
            using var to = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
            try
            {
                while (await daemon.Events.WaitToReadAsync(to.Token))
                    while (daemon.Events.TryRead(out var ev))
                        if (ev is LinkUp) return true;
            }
            catch (OperationCanceledException) { }
            return false;
        }
        Assert.True(await WaitForLinkUp());
        Assert.NotNull(clip.OnLocalChange);

        // Simulate a local 2-file copy through the daemon's wired handler.
        await clip.OnLocalChange!(new FilesClip(new List<(string, byte[])>
        {
            ("first.txt", "one"u8.ToArray()),
            ("second.txt", "two"u8.ToArray()),
        }));

        // Old peer receives a legacy single "file" clip (the first file).
        var got = await raw.ReceiveMessageAsync(cts.Token);
        Assert.Equal("clip", got!.Type);
        Assert.Equal("file", got.Kind);
        Assert.Equal("first.txt", got.Name);

        async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }
        Assert.True(await WaitUntil(() => { lock (notes) return notes.Any(n => n.Contains("1 file")); }));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DowngradeSkipToastAggregatedToOneAcrossOldPeers()
    {
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-agg-" + Guid.NewGuid());
        var clip = new FakeClipboard();
        var notes = new List<string>();
        var daemon = new Daemon(
            new DaemonConfig("agg-token", 28626, "agg", NotificationsEnabled: true),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
            primaryIPv4: () => "127.0.0.1",
            notify: (_, body) => { lock (notes) notes.Add(body); }, onFatal: _ => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = daemon.RunForeverAsync(cts.Token);

        async Task<FramedConnection> ConnectOld(string node, string name)
        {
            var raw = await ConnectWithRetry(28626, cts.Token);
            await raw.SendFrameAsync(WireMessage.Hello(
                Hashing.Sha256Hex("agg-token"), node, name, "1.0.0") with { ProtocolMinor = 0 },
                cts.Token);
            _ = await raw.ReceiveMessageAsync(cts.Token);
            return raw;
        }
        using var rawA = await ConnectOld("old-a", "old-a");
        using var rawB = await ConnectOld("old-b", "old-b");

        // Drain LinkUp events until both old peers are linked.
        async Task<bool> WaitLinks(int n, double seconds = 12)
        {
            int count = 0;
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                while (daemon.Events.TryRead(out var ev)) if (ev is LinkUp) count++;
                if (count >= n) return true;
                await Task.Delay(50);
            }
            return count >= n;
        }
        Assert.True(await WaitLinks(2));
        Assert.NotNull(clip.OnLocalChange);

        await clip.OnLocalChange!(new FilesClip(new List<(string, byte[])>
        {
            ("a.txt", "one"u8.ToArray()),
            ("b.txt", "two"u8.ToArray()),
            ("c.txt", "three"u8.ToArray()),
        }));

        async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }
        // ONE aggregated skip toast for the whole copy, not one per old peer.
        Assert.True(await WaitUntil(() =>
        { lock (notes) return notes.Count(n => n.Contains("not synced")) == 1; }));
        lock (notes) Assert.Contains(notes, n => n.Contains("2 file(s) not synced"));

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SizeSkipToastFiresOncePerClipEvenWhenNothingWasSent()
    {
        // A clip over the legacy 16 MiB cap is skipped on every pre-1.2 link, so
        // NOTHING is sent — the aggregated size toast must still fire (exactly
        // once), which an early "nothing sent, bail out" return would swallow.
        var stateDir = Path.Combine(Path.GetTempPath(), "anyclip-sizeskip-" + Guid.NewGuid());
        var clip = new FakeClipboard();
        var notes = new List<string>();
        var daemon = new Daemon(
            new DaemonConfig("size-token", 28627, "size", NotificationsEnabled: true),
            appVersion: "0.0.0-test", stateDir: stateDir,
            clipboard: clip, mdns: new FakeMdns(), pidLock: new FakePidLock(),
            primaryIPv4: () => "127.0.0.1",
            notify: (_, body) => { lock (notes) notes.Add(body); }, onFatal: _ => { });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var run = daemon.RunForeverAsync(cts.Token);

        using var raw = await ConnectWithRetry(28627, cts.Token);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("size-token"), "ffffffff-old", "old-peer", "1.0.0")
            with { ProtocolMinor = 0 }, cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token); // daemon hello

        async Task<bool> WaitLinkUp(double seconds = 12)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline)
            {
                while (daemon.Events.TryRead(out var ev)) if (ev is LinkUp) return true;
                await Task.Delay(50);
            }
            return false;
        }
        Assert.True(await WaitLinkUp());
        Assert.NotNull(clip.OnLocalChange);

        await clip.OnLocalChange!(new TextClip(new string('x', Wire.LegacyMaxPayload + 1024)));

        async Task<bool> WaitUntil(Func<bool> cond, double seconds = 5)
        {
            var deadline = DateTime.UtcNow.AddSeconds(seconds);
            while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
            return cond();
        }
        Assert.True(await WaitUntil(() =>
        { lock (notes) return notes.Any(n => n.Contains("clip not sent to")); }));
        lock (notes)
        {
            Assert.Equal(1, notes.Count(n => n.Contains("clip not sent to")));
            Assert.Contains(
                "clip not sent to old-peer (too large for its AnyClip version)", notes);
        }

        cts.Cancel();
        try { await run; } catch (OperationCanceledException) { }
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
