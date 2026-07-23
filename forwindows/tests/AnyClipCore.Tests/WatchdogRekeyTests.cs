using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class WatchdogRekeyTests
{
    private static async Task<bool> WaitUntil(Func<bool> cond, double seconds = 6)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private static async Task<FramedConnection> RawHandshake(
        int port, string node, string name, CancellationToken ct)
    {
        var raw = await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("tok"), node, name, "0.0.0-test"), ct);
        _ = await raw.ReceiveMessageAsync(ct);
        return raw;
    }

    [Fact]
    public async Task EscalatorRefreshesThenBouncesWhenZeroLinks()
    {
        var mdns = new FakeMdns();
        var m = new LinkManager(new LinkConfig("tok", 28731, "esc", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var wd = Watchdogs.IdleLinkWatchdogAsync(mdns, m, 0.2, 2, cts.Token);
        await Assert.ThrowsAsync<DaemonRestartException>(async () => await wd);
        Assert.True(mdns.Refreshes >= 2); // two refresh attempts before the bounce
    }

    [Fact]
    public async Task EscalatorNeverFiresWhileALinkIsActive()
    {
        var mdns = new FakeMdns();
        var m = new LinkManager(new LinkConfig("tok", 28732, "esc", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));
        using var raw = await RawHandshake(28732, "node-live", "live", cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        using var wdCts = new CancellationTokenSource();
        var wd = Watchdogs.IdleLinkWatchdogAsync(mdns, m, 0.2, 2, wdCts.Token);
        await Task.Delay(1000);
        Assert.Equal(0, mdns.Refreshes); // a live link -> escalator stays quiet
        Assert.False(wd.IsFaulted);

        wdCts.Cancel(); cts.Cancel(); m.Shutdown();
        try { await wd; } catch (OperationCanceledException) { }
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PerLinkStalenessDropsOnlyTheSilentLink()
    {
        var events = new List<DaemonEvent>();
        var m = new LinkManager(new LinkConfig("tok", 28733, "stale", "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant(), linkPingInterval: 0.3);
        m.OnClip = (_, _) => Task.CompletedTask;
        m.Emit = e => { lock (events) events.Add(e); };
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        // Live peer answers pings with pongs -> refreshes its inbound clock.
        var rawLive = await RawHandshake(28733, "node-live", "live", cts.Token);
        var pongPump = Task.Run(async () =>
        {
            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var msg = await rawLive.ReceiveMessageAsync(cts.Token);
                    if (msg is null) break;
                    if (msg.Type == "ping") await rawLive.SendFrameAsync(WireMessage.Pong(1), cts.Token);
                }
            }
            catch { }
        });
        // Silent peer never pongs.
        var rawSilent = await RawHandshake(28733, "node-silent", "silent", cts.Token);

        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));
        // 0.3s interval * deadFactor 3 = 0.9s of silence -> the silent link drops.
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1, 8));
        lock (events)
        {
            Assert.Contains(events, e => e is LinkDown d && d.NodeId == "node-silent");
            Assert.DoesNotContain(events, e => e is LinkDown d && d.NodeId == "node-live");
        }

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
        try { await pongPump; } catch { }
        rawLive.Dispose();
    }
}
