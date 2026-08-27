using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class LinkManagerTests
{
    private static LinkManager MakeManager(
        string token, int port, string name,
        List<(ClipPayload Payload, string Peer)> clips,
        List<DaemonEvent> events,
        int maxPeers = LinkManager.DefaultMaxPeers, double ping = 30)
    {
        var m = new LinkManager(
            new LinkConfig(token, port, name, "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant(), maxPeers, ping);
        m.OnClip = (p, peer) => { lock (clips) clips.Add((p, peer)); return Task.CompletedTask; };
        m.Emit = e => { lock (events) events.Add(e); };
        return m;
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

    // A raw wire peer that completes the handshake against the manager and then
    // stays open. `minor` sets its advertised protocol_minor.
    private static async Task<FramedConnection> RawHandshake(
        int port, string token, string nodeId, string name, int minor, CancellationToken ct)
    {
        var raw = await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct);
        var hello = WireMessage.Hello(Hashing.Sha256Hex(token), nodeId, name, "0.0.0-test")
            with { ProtocolMinor = minor };
        await raw.SendFrameAsync(hello, ct);
        _ = await raw.ReceiveMessageAsync(ct); // manager's hello
        return raw;
    }

    [Fact]
    public async Task TwoManagersHandshakeAndBroadcastClips()
    {
        var aClips = new List<(ClipPayload, string)>(); var aEvents = new List<DaemonEvent>();
        var bClips = new List<(ClipPayload, string)>(); var bEvents = new List<DaemonEvent>();
        var a = MakeManager("tok", 28711, "node-a", aClips, aEvents);
        var b = MakeManager("tok", 28712, "node-b", bClips, bEvents);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        await b.TryConnectAsync("127.0.0.1", 28711, "127.0.0.1:28711", cts.Token);
        Assert.True(await WaitUntil(() => a.ActiveLinkCount == 1 && b.ActiveLinkCount == 1));
        lock (aEvents) Assert.Contains(aEvents, e => e is LinkUp u && u.PeerName == "node-b");

        var res = await b.BroadcastAsync(new TextClip("from-b"));
        Assert.Equal(1, res.Sent);
        Assert.True(await WaitUntil(() =>
        { lock (aClips) return aClips.Any(c => c.Item1 is TextClip t && t.Text == "from-b"); }));
        // Source peer name threaded through the serialized apply.
        lock (aClips) Assert.Equal("node-b", aClips.First(c => c.Item1 is TextClip).Item2);

        cts.Cancel(); a.Shutdown(); b.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task WrongTokenRejectedWithAuthEvent()
    {
        var events = new List<DaemonEvent>();
        var a = MakeManager("right", 28713, "a", new(), events);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28713, 5, cts.Token);
        await raw.SendFrameAsync(WireMessage.Hello(
            Hashing.Sha256Hex("wrong"), "ffffffff-bad", "b", "0.0.0-test"), cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Any(e => e is HandshakeFailed { Reason: "auth" }); }));
        Assert.Equal(0, a.ActiveLinkCount);

        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MajorMismatchRefusedWithVersionEvent()
    {
        var events = new List<DaemonEvent>();
        var a = MakeManager("tok", 28714, "a", new(), events);
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => a.IsServing));

        using var raw = await FramedConnection.ConnectAsync("127.0.0.1", 28714, 5, cts.Token);
        var bad = WireMessage.Hello(Hashing.Sha256Hex("tok"), "ffffffff-v2", "future", "2.0.0")
            with { ProtocolMajor = 2 };
        await raw.SendFrameAsync(bad, cts.Token);
        _ = await raw.ReceiveMessageAsync(cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Any(e => e is HandshakeFailed h && h.Reason.StartsWith("version:")); }));
        Assert.Equal(0, a.ActiveLinkCount);

        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task ServeRetriesBindWhenPortTemporarilyHeld()
    {
        var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 28716);
        blocker.Start();
        var a = MakeManager("t", 28716, "retry", new(), new());
        using var cts = new CancellationTokenSource();
        var serveA = a.ServeAsync(cts.Token);
        await Task.Delay(700);
        blocker.Stop();
        Assert.True(await WaitUntil(() => a.IsServing, 5));
        cts.Cancel(); a.Shutdown();
        try { await serveA; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task NewNodeIdRefusedAtCapCountStable()
    {
        var events = new List<DaemonEvent>();
        var m = MakeManager("tok", 28717, "cap", new(), events, maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var raw1 = await RawHandshake(28717, "tok", "aaaa-node-1", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        // New node_id while at cap -> refused; count stays 1, no LinkUp for it.
        using var raw2 = await RawHandshake(28717, "tok", "bbbb-node-2", "peer-2", 1, cts.Token);
        await Task.Delay(400);
        Assert.Equal(1, m.ActiveLinkCount);
        lock (events) Assert.DoesNotContain(events, e => e is LinkUp u && u.NodeId == "bbbb-node-2");

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task KnownNodeIdReconnectAtCapReplacesWithoutSpuriousLinkDown()
    {
        var events = new List<DaemonEvent>();
        var m = MakeManager("tok", 28718, "dup", new(), events, maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        var raw1 = await RawHandshake(28718, "tok", "dup-node", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1
            && events.Any(e => e is LinkUp u && u.NodeId == "dup-node")));

        // Outside the race window: a fresh connection for the SAME node_id, even
        // at cap, is ROUTED (replaces the live session), not refused.
        await Task.Delay(1700);
        var raw2 = await RawHandshake(28718, "tok", "dup-node", "peer-2", 1, cts.Token);
        Assert.True(await WaitUntil(() =>
        { lock (events) return events.Count(e => e is LinkUp u && u.NodeId == "dup-node") >= 2; }));
        Assert.Equal(1, m.ActiveLinkCount);
        // Replaced session was superseded -> no LinkDown for that node_id.
        lock (events) Assert.DoesNotContain(events, e => e is LinkDown d && d.NodeId == "dup-node");

        raw1.Dispose(); raw2.Dispose();
        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task DeadLinkFreesCapSlotForNewPeer()
    {
        var m = MakeManager("tok", 28719, "cap", new(), new(), maxPeers: 1);
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        var raw1 = await RawHandshake(28719, "tok", "node-1", "peer-1", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));
        raw1.Dispose(); // link dies -> table entry removed immediately -> slot freed
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 0));

        using var raw2 = await RawHandshake(28719, "tok", "node-2", "peer-2", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task BroadcastDowngradesFilesForOldPeerAndSendsFilesForNew()
    {
        var m = MakeManager("tok", 28720, "bcast", new(), new());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var rawNew = await RawHandshake(28720, "tok", "new-node", "new", 1, cts.Token);
        using var rawOld = await RawHandshake(28720, "tok", "old-node", "old", 0, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<(string, byte[])>
        {
            ("a.txt", "one"u8.ToArray()),
            ("b.txt", "two"u8.ToArray()),
        }));
        Assert.Equal(2, res.Sent);
        Assert.Equal(1, res.OldPeerDrops); // 2 files -> 1 dropped for the minor-0 peer

        var fNew = await rawNew.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", fNew!.Kind);
        Assert.Equal(2, fNew.Files!.Count);
        var fOld = await rawOld.ReceiveMessageAsync(cts.Token);
        Assert.Equal("file", fOld!.Kind);           // downgraded to first file
        Assert.Equal("a.txt", fOld.Name);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task FilesClipInvalidOrEmptyFrameIgnoredAndLinkStaysUp()
    {
        var clips = new List<(ClipPayload, string)>();
        var m = MakeManager("tok", 28721, "a", clips, new());
        using var cts = new CancellationTokenSource();
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var raw = await RawHandshake(28721, "tok", "ffffffff-raw", "raw", 1, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        await raw.SendFrameAsync(new WireMessage
        { Type = "clip", Kind = "files", Files = new List<WireFileEntry>(), Hash = "x", Ts = 1 }, cts.Token);
        await raw.SendFrameAsync(new WireMessage
        {
            Type = "clip", Kind = "files",
            Files = new List<WireFileEntry>
            {
                new() { Name = "ok.txt", Content = Convert.ToBase64String("ok"u8.ToArray()), Hash = "x", Bytes = 2 },
                new() { Name = "bad.txt", Content = "!!!not-base64!!!", Hash = "x", Bytes = 0 },
            },
            Hash = "x", Ts = 1,
        }, cts.Token);
        await raw.SendFrameAsync(WireMessage.ClipFiles(
            new List<(string, byte[])> { ("a.txt", "aa"u8.ToArray()), ("b.txt", "bb"u8.ToArray()) },
            1), cts.Token);

        Assert.True(await WaitUntil(() =>
        { lock (clips) return clips.Any(c => c.Item1 is FilesClip f && f.Files.Count == 2); }));
        lock (clips) Assert.DoesNotContain(clips, c => c.Item1 is FilesClip f && f.Files.Count != 2);
        Assert.Equal(1, m.ActiveLinkCount);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public void FlattenNoticeMessageIsThePinnedWording()
    {
        // Pinned across all three implementations; logged once per clip per
        // affected link when the peer takes the frame but cannot rebuild it.
        Assert.Equal("peer old-pc will flatten folders (protocol < 1.3)",
            LinkManager.FlattenNoticeMessage("old-pc"));
    }

    [Fact]
    public void FolderOnlyNoticeMessageIsThePinnedWording()
    {
        // The other half of the pinned pair: what a minor-0 link gets INSTEAD
        // of a downgraded frame when every entry came out of a folder. Quoted
        // peer name, in lockstep with anyclip and Swift LinkManager.
        Assert.Equal("folder-only clip not sent to 'old-pc' (peer protocol 1.0)",
            LinkManager.FolderOnlyNoticeMessage("old-pc"));
    }

    [Fact]
    public async Task FolderOnlyClipSendsNothingToAMinorZeroPeer()
    {
        var m = MakeManager("tok", 28722, "folder", new(), new());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var oldPeer = await RawHandshake(28722, "tok", "old-node", "old", 0, cts.Token);
        using var modern = await RawHandshake(28722, "tok", "new-node", "new", 3, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<FileEntry>
        {
            new("a.txt", "one"u8.ToArray(), "docs/a.txt"),
            new("b.txt", "two"u8.ToArray(), "docs/sub/b.txt"),
        }));

        // A folder entry cannot ride the first-file kind:"file" fallback, so
        // the minor-0 link is skipped ENTIRELY — and kept up.
        Assert.Equal(new[] { "new" }, res.Delivered);
        Assert.Equal(0, res.OldPeerDrops);
        Assert.Empty(res.SizeSkipped);
        Assert.Equal(2, m.ActiveLinkCount);

        var got = await modern.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", got!.Kind);
        Assert.Equal("docs/a.txt", got.Files![0].Path);
        Assert.Equal("docs/sub/b.txt", got.Files[1].Path);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MinorZeroFallbackPicksTheFirstLooseFileAndMinorTwoGetsTheSameFrame()
    {
        var m = MakeManager("tok", 28723, "mixed", new(), new());
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var oldPeer = await RawHandshake(28723, "tok", "o-node", "old-mix", 0, cts.Token);
        using var mid = await RawHandshake(28723, "tok", "m-node", "mid-mix", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var res = await m.BroadcastAsync(new FilesClip(new List<FileEntry>
        {
            new("tree.txt", "one"u8.ToArray(), "docs/tree.txt"),
            new("loose.txt", "two"u8.ToArray()),
        }));
        Assert.Equal(2, res.Sent);
        Assert.Equal(1, res.OldPeerDrops);

        // Minor 0: the folder entry is excluded, so the fallback carries the
        // first LOOSE file, not files[0].
        var toOld = await oldPeer.ReceiveMessageAsync(cts.Token);
        Assert.Equal("file", toOld!.Kind);
        Assert.Equal("loose.txt", toOld.Name);

        // Minor 1-2: the SAME files frame, paths intact — the peer flattens
        // benignly because its strict decoder reads only name + content.
        var toMid = await mid.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", toMid!.Kind);
        Assert.Equal(2, toMid.Files!.Count);
        Assert.Equal("docs/tree.txt", toMid.Files[0].Path);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }
}
