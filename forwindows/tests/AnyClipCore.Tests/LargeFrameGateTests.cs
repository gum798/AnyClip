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
///
/// The exact gate log line is contract-pinned, so these tests need to read what
/// the manager wrote — and the manager logs through the process-wide
/// RotatingLog.Shared, like the live app. GateLog is the save/restore seam for
/// that global: it swaps in a throwaway file for the lifetime of this class and
/// puts the previous logger back afterwards.
public sealed class GateLog : IDisposable
{
    private readonly string _dir;
    private readonly RotatingLog _previous;
    public string Path { get; }

    public GateLog()
    {
        _dir = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "anyclip-gatelog-" + Guid.NewGuid());
        Directory.CreateDirectory(_dir);
        Path = System.IO.Path.Combine(_dir, "anyclip.log");
        _previous = RotatingLog.Shared;
        // maxBytes large enough that rotation can never roll an asserted line
        // out of the file, however chatty the rest of the suite gets.
        RotatingLog.Shared = new RotatingLog(Path, maxBytes: int.MaxValue);
    }

    /// FileShare.ReadWrite: RotatingLog keeps appending while we read.
    public string Text()
    {
        if (!File.Exists(Path)) return "";
        using var fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var r = new StreamReader(fs);
        return r.ReadToEnd();
    }

    public void Dispose()
    {
        RotatingLog.Shared = _previous;
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// Named collection shared with LinkManagerTests — see LogSeam.
public static class LogSeam
{
    public const string Name = "anyclip-log-seam";
}

[Collection(LogSeam.Name)]
public class LargeFrameGateTests(GateLog log) : IClassFixture<GateLog>
{
    // Every test in the class reads the SAME log file (xUnit runs a class's
    // tests serially but shares one fixture), so peer names are distinct per
    // test to keep the "contains" assertions unambiguous.
    private readonly GateLog _log = log;

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
        // Delivered names ONLY the peer that took the clip — the caller uses it
        // for the "-> sent" log/toast, which must not name a gate-skipped peer.
        Assert.Equal(new[] { "new-text" }, result.Delivered);
        Assert.Equal(new[] { "old-text" }, result.SizeSkipped);
        // Exact log line — the wording is part of the cross-implementation contract.
        Assert.Contains(
            "clip too large for 'old-text' (peer protocol < 1.2); skipping",
            _log.Text());
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
        Assert.Empty(result.Delivered);
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
        Assert.Equal(new[] { "new-small", "old-small" },
            result.Delivered.OrderBy(n => n, StringComparer.Ordinal));
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

    // ---- one frame per variant per broadcast ----------------------------

    // NECESSARY-but-not-sufficient proxy for the encode-once rule: every link
    // taking the same variant must see byte-identical frame content. The pre-fix
    // code built one WireMessage (and one clock read) PER LINK, so its `ts`
    // values differed and this fails; a hypothetical double encode off the ONE
    // hoisted `ts` would still pass. Proving the encode count itself would need
    // production instrumentation, which is not worth a counter on the hot path.
    [Fact]
    public async Task EveryLinkGetsTheSameFrameContentForOneVariant()
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
            _log.Text());
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

        // C#-only downgrade breadcrumb. Past tense and logged AFTER the send,
        // because the size gate can skip the link and "sending" would then be a
        // lie. Python/Swift have no counterpart line; pinning the shape here so
        // the wording is not changed again silently.
        Assert.Contains(
            "peer old-3 protocol minor 0 < 1: sent 1 of 3 files", _log.Text());

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
