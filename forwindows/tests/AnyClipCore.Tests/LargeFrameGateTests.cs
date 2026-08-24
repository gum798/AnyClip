using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// The per-link legacy send gate behind the 64 MiB frame cap (protocol 1.2).
///
/// Peers still on protocol < 1.2 enforce the old 16 MiB receive cap and CLOSE
/// the session on a bigger frame, so the broadcast fan-out must gate per link:
/// encode the payload variant chosen for that link once, and skip (never drop)
/// any link whose peer minor is < 2 when that frame exceeds Wire.LegacyMaxPayload.
/// Mirrors tests/test_large_frames.py and LargeFrameGateTests.swift.
public class LargeFrameGateTests
{
    private static LinkManager MakeManager(string token, int port, string name)
    {
        var m = new LinkManager(
            new LinkConfig(token, port, name, "0.0.0-test"),
            Guid.NewGuid().ToString().ToLowerInvariant());
        m.OnClip = (_, _) => Task.CompletedTask;
        m.Emit = _ => { };
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

    /// TCP connect + hello handshake against a serving manager, advertising `minor`.
    private static async Task<FramedConnection> RawPeer(
        int port, string token, string nodeId, string name, int minor, CancellationToken ct)
    {
        var raw = await FramedConnection.ConnectAsync("127.0.0.1", port, 5, ct);
        var hello = WireMessage.Hello(Hashing.Sha256Hex(token), nodeId, name, "0.0.0-test")
            with { ProtocolMinor = minor };
        await raw.SendFrameAsync(hello, ct);
        _ = await raw.ReceiveMessageAsync(ct); // manager's hello
        return raw;
    }

    /// Drain one frame from `conn` CONCURRENTLY with the broadcast that produces
    /// it. An over-legacy-cap frame is far bigger than the loopback socket
    /// buffer, so a peer that only reads after BroadcastAsync returns would park
    /// the send until its (size-scaled) budget expires and lose the link.
    private static async Task<(LinkManager.BroadcastResult Result, WireMessage? Got)>
        ReadingConcurrently(
            FramedConnection conn, Func<Task<LinkManager.BroadcastResult>> broadcast,
            CancellationToken ct)
    {
        var reader = Task.Run<WireMessage?>(async () =>
        {
            try { return await conn.ReceiveMessageAsync(ct); }
            catch { return null; }
        }, ct);
        var result = await broadcast();
        var got = await reader;
        return (result, got);
    }

    // Point the shared logger at ONE throwaway file so the gate's exact log line
    // can be asserted (the manager logs through RotatingLog.Shared, like the live
    // app). Initialized exactly once for the whole class, so parallel tests
    // cannot re-point the shared logger out from under each other mid-run —
    // hence the distinct peer names below, since every test reads the same file.
    private static readonly string GateLogPath = InitSharedLog();

    private static string InitSharedLog()
    {
        var dir = Path.Combine(Path.GetTempPath(), "anyclip-gatelog-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "anyclip.log");
        RotatingLog.Shared = new RotatingLog(path);
        return path;
    }

    /// FileShare.ReadWrite: RotatingLog keeps appending while we read.
    private static string SharedLogText()
    {
        if (!File.Exists(GateLogPath)) return "";
        using var fs = new FileStream(GateLogPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    /// A text payload whose encoded frame is guaranteed to exceed the legacy cap.
    private static string OverLegacyCapText() =>
        new('x', Wire.LegacyMaxPayload + 1024);

    /// Two files whose FIRST entry alone exceeds the legacy cap once base64'd
    /// (13 MB -> ~17.3 MB), so the minor-0 first-file fallback variant is the one
    /// that gets gated.
    private static List<(string, byte[])> BigFirstFilePair() => new()
    {
        ("big.bin", new byte[13_000_000]),
        ("small.txt", "hi"u8.ToArray()),
    };

    // ---- per-link legacy gate: simple clips -----------------------------

    [Fact]
    public async Task OversizeTextReachesOnlyTheProtocol12Peer()
    {
        _ = GateLogPath;
        var m = MakeManager("tok", 28741, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var old = await RawPeer(28741, "tok", "old-node", "old-text", 1, cts.Token);
        using var modern = await RawPeer(28741, "tok", "new-node", "new-text", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var big = OverLegacyCapText();
        var (result, got) = await ReadingConcurrently(
            modern, () => m.BroadcastAsync(new TextClip(big)), cts.Token);

        Assert.Equal(1, result.Sent);
        Assert.Equal(new[] { "old-text" }, result.SizeSkipped);
        // Exact log line — the wording is part of the cross-implementation contract.
        Assert.Contains(
            "clip too large for 'old-text' (peer protocol < 1.2); skipping",
            SharedLogText());
        // The skipped link is NOT dropped and NOT closed.
        Assert.Equal(2, m.ActiveLinkCount);
        // The 1.2 peer really receives the over-legacy-cap frame.
        Assert.Equal("text", got!.Kind);
        Assert.Equal(Wire.LegacyMaxPayload + 1024, got.Content!.Length);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MinorZeroPeerIsAlsoGatedOnSimpleClips()
    {
        var m = MakeManager("tok", 28742, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var ancient = await RawPeer(28742, "tok", "anc-node", "ancient", 0, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 1));

        var result = await m.BroadcastAsync(new TextClip(OverLegacyCapText()));
        Assert.Equal(0, result.Sent);
        Assert.Equal(new[] { "ancient" }, result.SizeSkipped);
        Assert.Equal(1, m.ActiveLinkCount);   // link kept

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task UnderTheLegacyCapEveryoneGetsTheClip()
    {
        var m = MakeManager("tok", 28743, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var old = await RawPeer(28743, "tok", "o-node", "old-small", 0, cts.Token);
        using var modern = await RawPeer(28743, "tok", "n-node", "new-small", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var result = await m.BroadcastAsync(new TextClip("hello"));
        Assert.Equal(2, result.Sent);
        Assert.Empty(result.SizeSkipped);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task OversizeImageIsGatedToo()
    {
        var m = MakeManager("tok", 28744, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var old = await RawPeer(28744, "tok", "o-node", "old-image", 1, cts.Token);
        using var modern = await RawPeer(28744, "tok", "n-node", "new-image", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var png = new byte[13_000_000];  // base64 ~17.3 MB > legacy cap
        var (result, got) = await ReadingConcurrently(
            modern, () => m.BroadcastAsync(new ImageClip(png)), cts.Token);

        Assert.Equal(1, result.Sent);
        Assert.Equal(new[] { "old-image" }, result.SizeSkipped);
        Assert.Equal(2, m.ActiveLinkCount);
        Assert.Equal("image", got!.Kind);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    // ---- encode-once per broadcast --------------------------------------

    // A single encode per payload variant means a single `ts` for the whole
    // fan-out; the pre-fix code built one WireMessage (and one clock read) PER
    // LINK, so two peers could never be guaranteed the identical timestamp.
    [Fact]
    public async Task OnePayloadVariantIsEncodedOncePerBroadcast()
    {
        var m = MakeManager("tok", 28745, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var p1 = await RawPeer(28745, "tok", "n1-node", "p1", 2, cts.Token);
        using var p2 = await RawPeer(28745, "tok", "n2-node", "p2", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        _ = await m.BroadcastAsync(new TextClip("shared"));
        var g1 = await p1.ReceiveMessageAsync(cts.Token);
        var g2 = await p2.ReceiveMessageAsync(cts.Token);
        Assert.Equal("shared", g1!.Content);
        Assert.Equal("shared", g2!.Content);
        Assert.NotNull(g1.Ts);
        Assert.Equal(g1.Ts, g2.Ts);   // same encoded frame handed to both links

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    // ---- per-link legacy gate: files variants ---------------------------

    // The minor-0 link takes the first-file "file" fallback; when THAT variant is
    // over the legacy cap it is the one gated, while the minor-2 link still gets
    // the full "files" frame.
    [Fact]
    public async Task FirstFileFallbackVariantIsGatedForAMinorZeroPeer()
    {
        _ = GateLogPath;
        var m = MakeManager("tok", 28746, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var old = await RawPeer(28746, "tok", "o-node", "old-files", 0, cts.Token);
        using var modern = await RawPeer(28746, "tok", "n-node", "new-files", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var (result, got) = await ReadingConcurrently(
            modern, () => m.BroadcastAsync(new FilesClip(BigFirstFilePair())), cts.Token);

        Assert.Equal(1, result.Sent);
        Assert.Equal(new[] { "old-files" }, result.SizeSkipped);
        // Nothing reached the old peer, so no first-file-fallback toast either.
        Assert.Equal(0, result.OldPeerDrops);
        Assert.Equal(2, m.ActiveLinkCount);
        Assert.Contains(
            "clip too large for 'old-files' (peer protocol < 1.2); skipping",
            SharedLogText());
        Assert.Equal("files", got!.Kind);
        Assert.Equal(2, got.Files!.Count);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task MinorOnePeerIsGatedOnAnOversizeFilesClip()
    {
        var m = MakeManager("tok", 28747, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        // minor 1 takes kind:"files" but still enforces the 16 MiB receive cap.
        using var mid = await RawPeer(28747, "tok", "m-node", "mid", 1, cts.Token);
        using var modern = await RawPeer(28747, "tok", "n-node", "new-mid", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var (result, got) = await ReadingConcurrently(
            modern, () => m.BroadcastAsync(new FilesClip(BigFirstFilePair())), cts.Token);

        Assert.Equal(1, result.Sent);
        Assert.Equal(new[] { "mid" }, result.SizeSkipped);
        Assert.Equal("files", got!.Kind);
        Assert.Equal(2, m.ActiveLinkCount);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    [Fact]
    public async Task SmallFilesClipStillFansOutWithMinorGating()
    {
        var m = MakeManager("tok", 28748, "a");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var serve = m.ServeAsync(cts.Token);
        Assert.True(await WaitUntil(() => m.IsServing));

        using var old = await RawPeer(28748, "tok", "o-node", "old-3", 0, cts.Token);
        using var modern = await RawPeer(28748, "tok", "n-node", "new-3", 2, cts.Token);
        Assert.True(await WaitUntil(() => m.ActiveLinkCount == 2));

        var result = await m.BroadcastAsync(new FilesClip(new List<(string, byte[])>
        {
            ("a.txt", "one"u8.ToArray()),
            ("b.txt", "two"u8.ToArray()),
            ("c.txt", "three"u8.ToArray()),
        }));
        Assert.Equal(2, result.Sent);
        Assert.Empty(result.SizeSkipped);
        Assert.Equal(2, result.OldPeerDrops);   // the minor-0 peer took only the first file

        var gOld = await old.ReceiveMessageAsync(cts.Token);
        Assert.Equal("file", gOld!.Kind);
        Assert.Equal("a.txt", gOld.Name);
        var gNew = await modern.ReceiveMessageAsync(cts.Token);
        Assert.Equal("files", gNew!.Kind);
        Assert.Equal(3, gNew.Files!.Count);

        cts.Cancel(); m.Shutdown();
        try { await serve; } catch (OperationCanceledException) { }
    }

    // ---- aggregated skip toast ------------------------------------------

    [Fact]
    public void SizeSkipMessageIsAtMostOnePerClip()
    {
        Assert.Null(Daemon.SizeSkipMessage(Array.Empty<string>()));
        Assert.Equal("clip not sent to MacBook (too large for its AnyClip version)",
            Daemon.SizeSkipMessage(new[] { "MacBook" }));
        Assert.Equal("clip not sent to 3 peer(s) (too large for their AnyClip version)",
            Daemon.SizeSkipMessage(new[] { "MacBook", "PC", "NUC" }));
    }
}
