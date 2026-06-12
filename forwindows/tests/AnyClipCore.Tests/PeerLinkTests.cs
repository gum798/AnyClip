using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerLinkTests
{
    private static (PeerLink Link, List<ClipPayload> Clips, List<DaemonEvent> Events)
        MakeLink(string token, int port, string name)
    {
        var clips = new List<ClipPayload>();
        var events = new List<DaemonEvent>();
        var link = new PeerLink(
            new PeerLink.LinkConfig(token, port, name, "0.0.0-test"),
            Guid.NewGuid().ToString());
        link.OnClip = p => { lock (clips) clips.Add(p); return Task.CompletedTask; };
        link.Emit = e => { lock (events) events.Add(e); };
        return (link, clips, events);
    }

    private static async Task<bool> WaitUntil(Func<bool> cond, double timeoutSeconds = 5)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            if (cond()) return true;
            await Task.Delay(50);
        }
        return cond();
    }

    [Fact]
    public async Task TwoLinksHandshakeAndExchangeClips()
    {
        var (a, aClips, aEvents) = MakeLink("tok", 28611, "node-a");
        var (b, bClips, _) = MakeLink("tok", 28612, "node-b");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        _ = b.TryConnectAsync("127.0.0.1", 28611, "127.0.0.1:28611", cts.Token);
        Assert.True(await WaitUntil(() => a.IsActive && b.IsActive));
        Assert.Equal("node-b", a.PeerName);
        Assert.Equal("node-a", b.PeerName);
        lock (aEvents) Assert.Contains(aEvents, e => e is LinkUp);

        await b.SendClipAsync(new TextClip("from-b"));
        Assert.True(await WaitUntil(() =>
        {
            lock (aClips) return aClips.Any(c => c is TextClip t && t.Text == "from-b");
        }));

        await a.SendClipAsync(new ImageClip(new byte[] { 1, 2, 3 }));
        Assert.True(await WaitUntil(() =>
        {
            lock (bClips) return bClips.Any(c =>
                c is ImageClip i && i.Png.SequenceEqual(new byte[] { 1, 2, 3 }));
        }));

        cts.Cancel();
        a.Shutdown(); b.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task WrongTokenRejectedWithAuthEvent()
    {
        var (a, _, aEvents) = MakeLink("right", 28613, "a");
        var (b, _, _) = MakeLink("wrong", 28614, "b");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        await b.TryConnectAsync("127.0.0.1", 28613, "127.0.0.1:28613", cts.Token);
        Assert.True(await WaitUntil(() =>
        {
            lock (aEvents) return aEvents.Any(e =>
                e is HandshakeFailed { Reason: "auth" });
        }));
        Assert.False(a.IsActive);
        cts.Cancel(); a.Shutdown(); b.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task PingAnsweredWithPongAndMajorMismatchRefused()
    {
        var (a, _, aEvents) = MakeLink("tok", 28615, "a");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        // Raw client completes the handshake manually, sends ping.
        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28615, 5, cts.Token);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("tok"), "ffffffff-raw", "raw", "0.0.0-test"), cts.Token);
        var serverHello = await raw.ReceiveMessageAsync(cts.Token);
        Assert.Equal("hello", serverHello!.Type);
        await raw.SendFrameAsync(WireMessage.Ping(1), cts.Token);
        var reply = await raw.ReceiveMessageAsync(cts.Token);
        Assert.Equal("pong", reply!.Type);
        raw.Dispose();
        Assert.True(await WaitUntil(() => !a.IsActive));

        // Major-mismatch hello is refused with a version: event.
        using var raw2 = await FramedConnection.ConnectAsync("127.0.0.1", 28615, 5, cts.Token);
        var badHello = WireMessage.Hello(
            Hashing.Sha256Hex("tok"), "ffffffff-v2", "future", "2.0.0")
            with { ProtocolMajor = 2 };
        await raw2.SendFrameAsync(badHello, cts.Token);
        _ = await raw2.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        {
            lock (aEvents) return aEvents.Any(e =>
                e is HandshakeFailed h && h.Reason.StartsWith("version:"));
        }));
        Assert.False(a.IsActive);
        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServeRetriesBindWhenPortTemporarilyHeld()
    {
        var blocker = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Any, 28616);
        blocker.Start();
        var (a, _, _) = MakeLink("t", 28616, "retry");
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        await Task.Delay(700);
        blocker.Stop();
        Assert.True(await WaitUntil(() => a.IsServing, 5));
        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }
}
