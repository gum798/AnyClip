using System.Diagnostics;

namespace AnyClip.Core;

/// One established peer link: the post-handshake session, keepalive, per-link
/// staleness clock, and per-link send. LinkManager performs the hello exchange,
/// the gate, and routing, then hands the open connection + parsed hello here;
/// this class NEVER re-reads a hello. Port of the narrowed anyclip.py PeerLink
/// (session half). Exactly one connection, one peer, for the link's lifetime.
public sealed class PeerLink
{
    // Process-wide monotonic seconds. Uses Stopwatch.GetTimestamp() (one fixed
    // epoch shared by every caller) rather than a per-class Stopwatch instance,
    // so LinkedAt recorded here is directly comparable against the same clock in
    // LinkManager's race-window check. A per-instance Stopwatch would give each
    // class its own epoch (and beforefieldinit could start them lazily on first
    // use), making that cross-object subtraction meaningless.
    private static double MonotonicNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    private static double UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    private readonly FramedConnection _conn;
    private volatile bool _alive = true;
    private readonly double _linkedAt = MonotonicNow();
    // Monotonic timestamp of the last inbound frame. Drives half-open detection:
    // a peer that slept without RST/FIN keeps the socket "writable" yet sends
    // nothing back, so staleness is judged from inbound silence, not send errors.
    private double _lastInboundAt = MonotonicNow();

    public string PeerNodeId { get; }
    public string PeerName { get; }
    public int PeerProtocolMinor { get; }
    public string? RemoteHost => _conn.RemoteIp;
    /// The host:port label this link was dialed with (outbound), else null
    /// (inbound). LinkManager uses it to avoid redialing an already-linked peer.
    public string? DialLabel { get; }
    public double LinkedAt => _linkedAt;
    public bool IsActive => _alive;

    public Func<ClipPayload, Task>? OnClip { get; set; }

    public PeerLink(string peerNodeId, string peerName, VersionInfo peerVersion,
        FramedConnection conn, bool inbound, string? dialLabel)
    {
        PeerNodeId = peerNodeId;
        PeerName = peerName;
        PeerProtocolMinor = peerVersion.ProtocolMinor;
        _conn = conn;
        _ = inbound; // LinkManager logs inbound/outbound at registration; kept in the API.
        DialLabel = dialLabel;
    }

    /// The receive loop. LinkManager emits LinkUp synchronously at registration
    /// (before this detached session starts), so this method only pumps frames
    /// until the connection closes. LinkDown is emitted by LinkManager.RunLinkAsync's
    /// finally — under the table lock, gated by the same identity check that guards
    /// table removal — so a link replaced under one node_id never emits LinkDown
    /// after its replacement's LinkUp (symmetric with the under-lock LinkUp).
    public async Task RunSessionAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                WireMessage? msg;
                try { msg = await _conn.ReceiveMessageAsync(ct); }
                catch { break; }
                _lastInboundAt = MonotonicNow();      // any frame proves the peer is alive
                if (msg is null) break;
                switch (msg.Type)
                {
                    case "clip":
                        await HandleClipAsync(msg);
                        break;
                    case "ping":
                        try { await _conn.SendFrameAsync(WireMessage.Pong(UnixNow()), ct); }
                        catch (Exception e)
                        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
                        break;
                    case "pong":
                        break; // presence is enough
                    default:
                        RotatingLog.Shared.Debug($"ignoring message type: {msg.Type}");
                        break;
                }
            }
        }
        finally
        {
            _alive = false;
            RotatingLog.Shared.Info($"peer {PeerName} disconnected");
            // LinkDown is emitted by LinkManager.RunLinkAsync (under the table
            // lock, gated by the same identity check that guards removal), not
            // here — a superseded link must not emit LinkDown for a node_id whose
            // replacement is already linked.
        }
    }

    private async Task HandleClipAsync(WireMessage msg)
    {
        var kind = msg.Kind ?? "text";
        switch (kind)
        {
            case "text" when msg.Content is not null:
                await (OnClip?.Invoke(new TextClip(msg.Content)) ?? Task.CompletedTask);
                break;
            case "image" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } png)
                    await (OnClip?.Invoke(new ImageClip(png)) ?? Task.CompletedTask);
                else RotatingLog.Shared.Warning("bad image payload from peer");
                break;
            case "file" when msg.Content is not null:
                if (WireMessage.StrictBase64Decode(msg.Content) is { } data)
                {
                    var name = string.IsNullOrEmpty(msg.Name) ? "received.bin" : msg.Name!;
                    await (OnClip?.Invoke(new FileClip(name, data)) ?? Task.CompletedTask);
                }
                else RotatingLog.Shared.Warning("bad file payload from peer");
                break;
            case "files":
                if (msg.Files is null || msg.Files.Count == 0)
                {
                    RotatingLog.Shared.Warning("ignoring files clip with no entries");
                    break;
                }
                var decoded = new List<FileEntry>(msg.Files.Count);
                bool bad = false;
                foreach (var entry in msg.Files)
                {
                    if (entry.Content is null ||
                        WireMessage.StrictBase64Decode(entry.Content) is not { } fbytes)
                    {
                        RotatingLog.Shared.Warning("bad file payload in files clip; ignoring frame");
                        bad = true;
                        break;
                    }
                    var fname = string.IsNullOrEmpty(entry.Name) ? "received.bin" : entry.Name!;
                    // The wire "path" rides through UNVALIDATED: validation belongs
                    // to the placement step, which falls back to flat for a bad
                    // path. Rejecting here would drop the whole frame.
                    decoded.Add(new FileEntry(fname, fbytes, entry.Path));
                    // hash NOT trusted from wire; recomputed downstream
                }
                if (!bad)
                    await (OnClip?.Invoke(new FilesClip(decoded)) ?? Task.CompletedTask);
                break;
            default:
                RotatingLog.Shared.Debug($"ignoring clip with kind={kind}");
                break;
        }
    }

    /// Per-link send of one ALREADY-encoded frame — the only clip-send path.
    /// The mesh broadcast encodes each payload variant once (guarding the 64 MiB
    /// cap) and reuses the bytes for the per-link legacy size gate and every
    /// send of that variant, so an N-peer fan-out never re-encodes a clip.
    ///
    /// Returns false when the link looks down: either it was already unlinked,
    /// or a hard send error DROPPED it (disposing the connection wakes the
    /// receive loop -> session tears down -> LinkManager removes it), so a
    /// broadcast failure isolates to this link. A frame the per-link legacy gate
    /// skips never reaches here at all — LinkManager.BroadcastAsync keeps that
    /// link up and records the peer for the aggregated skip toast.
    public async Task<bool> SendEncodedAsync(EncodedFrame frame)
    {
        if (!_alive) return false;
        try
        {
            await _conn.SendFrameAsync(frame, CancellationToken.None);
            return true;
        }
        catch (Exception e)
        {
            RotatingLog.Shared.Info($"send to {PeerName} failed; dropping link: {e.Message}");
            _conn.Dispose();
            return false;
        }
    }

    public async Task SendPingAsync()
    {
        if (!_alive) return;
        try { await _conn.SendFrameAsync(WireMessage.Ping(UnixNow()), CancellationToken.None); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"ping to {PeerName} failed: {e.Message}"); }
    }

    /// Seconds since the last inbound frame, or null if not linked. The per-link
    /// heartbeat compares this against its deadline.
    public double? SecondsSinceInbound() => _alive ? MonotonicNow() - _lastInboundAt : null;

    /// Drop a half-open link that has gone silent. Disposing the connection
    /// wakes the parked receive; the session loop tears down and clears the
    /// link. No-op if already unlinked.
    public void DropStaleLink(double idleSeconds)
    {
        if (!_alive) return;
        RotatingLog.Shared.Info(
            $"link to {PeerName} idle {(int)idleSeconds}s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect");
        _conn.Dispose();
    }

    public void Dispose()
    {
        _alive = false;
        _conn.Dispose();
    }
}
