using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AnyClip.Core;

/// Raised when the daemon cannot start and retrying will not help. Relocated
/// from PeerLink alongside the listening socket, which now lives here.
public sealed class FatalStartupException(string message) : Exception(message);

/// Per-link configuration shared by the manager. Moved out of PeerLink (which no
/// longer connects). Token-only config lives in config.json; the cap is a
/// constant + Python-only CLI flag, not persisted.
public sealed record LinkConfig(string Token, int Port, string Name, string AppVersion);

/// Owns the listening socket, the pre-routing gate (AuthGate ip-block/record +
/// token + version + self/loopback drop), and the active-link table keyed by
/// node_id. Accepts inbound and dials outbound; BOTH paths exchange hellos, run
/// the gate BEFORE any routing, then route to a per-peer PeerLink (create /
/// replace / refuse) handing it the open connection + parsed hello. Broadcasts
/// local clips to every active link with per-link protocol-minor gating, and
/// serializes received applies through one queue. Port of the LinkManager split
/// of anyclip.py PeerLink. The registration critical sections take a plain lock
/// and contain no awaits, mirroring the asyncio.Lock registration block.
public sealed class LinkManager
{
    public const int DefaultMaxPeers = 8;

    /// Per-copy broadcast result: the names of the peers that actually TOOK the
    /// (possibly downgraded) clip, the largest old-peer file-drop count for the
    /// aggregated fallback toast, and the peers the legacy size gate skipped for
    /// the aggregated size toast. Peers in SizeSkipped keep their links; the
    /// caller emits ONE toast.
    ///
    /// Delivered — not the manager's full link table — is what the caller must
    /// name in the "-&gt; sent ... to X" log line and the "AnyClip → X" toast: the
    /// per-link size gate means an active link can deliberately receive nothing,
    /// and naming it would contradict the skip toast fired for the same clip.
    /// Parity with Swift BroadcastResult.delivered.
    public readonly record struct BroadcastResult(
        IReadOnlyList<string> Delivered, int OldPeerDrops, IReadOnlyList<string> SizeSkipped)
    {
        /// How many links took the clip.
        public int Sent => Delivered.Count;
    }

    /// Logged once per clip per affected link: the peer takes the kind:"files"
    /// frame but is on protocol &lt; 1.3, so its strict decoder reads only name +
    /// content and the tree lands flat. Pinned wording across all three
    /// implementations. Keep in lockstep with anyclip's broadcast_files and
    /// Swift LinkManager.broadcast.
    public static string FlattenNoticeMessage(string peerName) =>
        $"peer {peerName} will flatten folders (protocol < 1.3)";

    /// Logged instead of a downgraded frame when EVERY entry of a files clip
    /// came out of a copied folder and the peer is on protocol 1.0: the legacy
    /// kind:"file" frame has nowhere to put a path, so a folder entry would land
    /// loose and unlabelled. Nothing is sent on that link and the link stays up.
    /// Pinned wording, in lockstep with anyclip and Swift.
    public static string FolderOnlyNoticeMessage(string peerName) =>
        $"folder-only clip not sent to '{peerName}' (peer protocol 1.0)";

    private static double UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    // Process-wide monotonic seconds sharing ONE epoch with PeerLink.MonotonicNow
    // (both call Stopwatch.GetTimestamp), so `MonotonicNow() - existing.LinkedAt`
    // is a true elapsed time. A per-class Stopwatch would give each type its own
    // epoch — and beforefieldinit could start LinkManager's lazily on the first
    // race check — making that subtraction garbage (race spuriously true).
    private static double MonotonicNow() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private readonly LinkConfig _config;
    private readonly string _nodeId;
    private readonly int _maxPeers;
    private readonly double _linkPingInterval;
    private readonly string _tokenHash;
    private readonly AuthGate _authGate = new();
    private readonly object _lock = new();                    // guards _links, _authGate, _connecting
    private readonly SemaphoreSlim _applyLock = new(1, 1);    // serializes received applies
    private readonly Dictionary<string, PeerLink> _links = new();
    private readonly HashSet<string> _connecting = new();
    private TcpListener? _listener;

    public LinkManager(LinkConfig config, string nodeId,
        int maxPeers = DefaultMaxPeers, double linkPingInterval = 30)
    {
        _config = config;
        _nodeId = nodeId;
        _maxPeers = maxPeers;
        _linkPingInterval = linkPingInterval;
        _tokenHash = Hashing.Sha256Hex(config.Token);
    }

    /// Serialized received-clip sink: (payload, source peer display name). All
    /// links funnel through here under one lock so applies never interleave —
    /// the single-slot-per-kind suppressor stays sufficient with N peers.
    public Func<ClipPayload, string, Task>? OnClip { get; set; }
    public Action<DaemonEvent>? Emit { get; set; }
    public volatile bool IsServing;

    public int ActiveLinkCount { get { lock (_lock) return _links.Count; } }
    public bool AtCap { get { lock (_lock) return _links.Count >= _maxPeers; } }

    // No LinkedPeerNames accessor: the "sent" log/toast must name only the peers
    // a clip was actually DELIVERED to (BroadcastResult.Delivered) — the
    // per-link size gate can leave an active link with nothing — and the tray
    // tracks names off the LinkUp/LinkDown events instead.

    /// True if any active link's remote IP is `host`. On a LAN a peer == one IP,
    /// so the reconnect loop uses this to avoid dialing a peer we already mesh
    /// with (inbound or outbound).
    public bool HasLinkToHost(string host)
    { lock (_lock) return _links.Values.Any(l => l.RemoteHost == host); }

    public async Task ServeAsync(CancellationToken ct)
    {
        if (_listener is not null)
            throw new FatalStartupException("ServeAsync called twice on the same LinkManager");

        TcpListener? listener = null;
        for (int attempt = 0; ; attempt++)
        {
            listener = new TcpListener(IPAddress.Any, _config.Port);
            // POSIX-only SO_REUSEADDR (parity with asyncio.start_server / macOS);
            // skipped on Windows where it has port-hijack semantics. Never permits
            // binding over an active LISTEN, so the bind-retry test still holds.
            if (!OperatingSystem.IsWindows())
                listener.Server.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try { listener.Start(); break; }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Stop();
                if (attempt >= 4)
                    throw new FatalStartupException(
                        $"port {_config.Port} still in use after cleanup attempt; "
                        + "another process may have grabbed it");
                RotatingLog.Shared.Info(
                    $"tcp/{_config.Port} still in use; retrying bind ({attempt + 1}/4)");
                await Task.Delay(500, ct);
            }
        }
        _listener = listener;
        IsServing = true;
        RotatingLog.Shared.Info($"listening on tcp/{_config.Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (SocketException e) when (e.SocketErrorCode != SocketError.OperationAborted)
                { continue; }
                catch (Exception e) when (
                    e is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // Listen socket aborted out from under us (sleep/resume, NIC
                    // reset) without a clean Stop(). Throw the restart sentinel so
                    // the supervisor rebinds — otherwise RunOnceAsync's WhenAny sees
                    // a RanToCompletion serve task and silently exits with tcp/24816
                    // unbound (the Windows wedge).
                    if (!ct.IsCancellationRequested)
                        throw new DaemonRestartException(
                            $"listener accept aborted ({e.GetType().Name}); rebinding daemon "
                            + "(likely sleep/resume or network change)");
                    break;
                }
                _ = Task.Run(() => HandleInboundAsync(socket, ct), ct);
            }
        }
        finally { listener.Stop(); _listener = null; IsServing = false; }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        FramedConnection framed;
        try { framed = new FramedConnection(socket); }
        catch (SocketException) { socket.Dispose(); return; }
        RotatingLog.Shared.Debug($"inbound from {framed.RemoteIp ?? "?"}");
        bool blocked;
        lock (_lock) blocked = framed.RemoteIp is not null && _authGate.IsBlocked(framed.RemoteIp);
        if (blocked)
        {
            RotatingLog.Shared.Info(
                $"auth gate: {framed.RemoteIp} blocked (>{AuthGate.MaxFails} failures, "
                + $"cooldown {(int)AuthGate.CooldownSeconds}s)");
            framed.Dispose();
            return;
        }
        try { await HandleConnectionAsync(framed, inbound: true, label: null, ct); }
        catch (Exception e) { RotatingLog.Shared.Debug($"inbound session ended: {e.Message}"); }
    }

    public async Task TryConnectAsync(string host, int port, string label, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_links.Count >= _maxPeers) return;                      // no slots for a new peer
            if (_links.Values.Any(l => l.DialLabel == label)) return;   // already linked to this label
            if (!_connecting.Add(label))                                // dedupe in-flight dials
            { RotatingLog.Shared.Debug($"connect to {label} already in flight, skipping"); return; }
        }
        try
        {
            FramedConnection framed;
            try { framed = await FramedConnection.ConnectAsync(host, port, Wire.ConnectTimeoutSeconds, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            { RotatingLog.Shared.Info($"connect to {label} failed: {e.Message}"); return; }
            RotatingLog.Shared.Debug($"outbound connected to {label}");
            try { await HandleConnectionAsync(framed, inbound: false, label, ct); }
            catch (Exception e) { RotatingLog.Shared.Debug($"outbound session ended: {e.Message}"); }
        }
        finally { lock (_lock) _connecting.Remove(label); }
    }

    // Shared inbound/outbound flow: hello exchange -> gate -> route. Returns once
    // the link is registered (or rejected); the session runs detached so the
    // caller (accept loop / reconnect loop) is freed to service other peers.
    private async Task HandleConnectionAsync(
        FramedConnection framed, bool inbound, string? label, CancellationToken ct)
    {
        try { await framed.SendFrameAsync(WireMessage.Hello(
            _tokenHash, _nodeId, _config.Name, _config.AppVersion), ct); }
        catch { framed.Dispose(); return; }
        string addr = framed.RemoteIp ?? "";

        WireMessage? hello;
        try
        {
            using var hs = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hs.CancelAfter(TimeSpan.FromSeconds(Wire.HandshakeTimeoutSeconds));
            hello = await framed.ReceiveMessageAsync(hs.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RotatingLog.Shared.Warning("handshake timeout");
            Emit?.Invoke(new HandshakeFailed(addr, "timeout"));
            framed.Dispose(); return;
        }
        catch { framed.Dispose(); return; }

        if (hello is null || hello.Type != "hello")
        {
            RotatingLog.Shared.Warning("invalid hello, closing");
            Emit?.Invoke(new HandshakeFailed(addr, "invalid"));
            framed.Dispose(); return;
        }

        // --- Gate (before any routing) ---
        string? peerIp = inbound ? framed.RemoteIp : null;
        if (hello.Token != _tokenHash)
        {
            RotatingLog.Shared.Warning($"auth failed from peer name={hello.Name ?? "?"}");
            if (peerIp is not null) lock (_lock) _authGate.RecordFail(peerIp);
            Emit?.Invoke(new HandshakeFailed(peerIp ?? addr, "auth"));
            framed.Dispose(); return;
        }
        var peerVersion = hello.PeerVersionInfo();
        var localVersion = new VersionInfo(_config.AppVersion, Wire.ProtocolMajor, Wire.ProtocolMinor);
        var compat = VersionNegotiator.Negotiate(localVersion, peerVersion);
        if (!VersionNegotiator.LinkAllowed(compat))
        {
            RotatingLog.Shared.Warning(
                $"version refused: local proto={Wire.ProtocolMajor}.{Wire.ProtocolMinor} "
                + $"vs peer proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor} "
                + $"app={peerVersion.AppVersion} -> {VersionNegotiator.WireValue(compat)}");
            Emit?.Invoke(new HandshakeFailed(addr, $"version:{VersionNegotiator.WireValue(compat)}"));
            framed.Dispose(); return;
        }
        if (compat != Compatibility.Compatible)
            RotatingLog.Shared.Info($"version mismatch (link kept): {VersionNegotiator.WireValue(compat)}");
        var peerId = hello.NodeId;
        if (string.IsNullOrEmpty(peerId) || peerId == _nodeId)
        {
            RotatingLog.Shared.Debug("self loopback or bad node_id, dropping");
            framed.Dispose(); return;
        }
        if (peerIp is not null) lock (_lock) _authGate.RecordOk(peerIp);

        // --- Route (register / replace / cap; no awaits in the critical section) ---
        string displayName = string.IsNullOrEmpty(hello.Name)
            ? peerId[..Math.Min(8, peerId.Length)] : hello.Name!;
        PeerLink link;
        lock (_lock)
        {
            if (_links.TryGetValue(peerId, out var existing))
            {
                // Duplicate connection for a live node_id. Inside the handshake
                // window it's a genuine simultaneous-connect race -> tie-break
                // (lexicographic). Outside, the peer considers the old link dead
                // (our side half-open) -> replace, never defend.
                bool race = MonotonicNow() - existing.LinkedAt < Wire.RaceWindowSeconds;
                if (race)
                {
                    bool keepThisLink =
                        (!inbound && string.CompareOrdinal(_nodeId, peerId) < 0) ||
                        (inbound && string.CompareOrdinal(_nodeId, peerId) > 0);
                    if (!keepThisLink)
                    { RotatingLog.Shared.Debug("tie-breaker: dropping duplicate link (race)"); framed.Dispose(); return; }
                    RotatingLog.Shared.Debug("tie-breaker: replacing existing link (race)");
                }
                else
                {
                    RotatingLog.Shared.Info($"replacing link with {existing.PeerName} (peer reconnected)");
                }
                existing.Dispose();
                _links.Remove(peerId);
                // No LinkDown for the replaced link: RunLinkAsync's finally gates
                // its emit on ReferenceEquals(cur, link), and the table entry for
                // peerId is about to point at the fresh link — so the old session's
                // teardown finds a different current link and stays silent.
            }
            else if (_links.Count >= _maxPeers)
            {
                // Cap is checked AFTER node_id routing, so a known peer reconnect
                // (handled above) is never refused; only a NEW node_id is.
                RotatingLog.Shared.Info($"peer cap reached ({_maxPeers}); refusing {displayName}");
                framed.Dispose(); return;
            }
            link = new PeerLink(peerId, displayName, peerVersion, framed, inbound, label);
            link.OnClip = p => DispatchReceiveAsync(p, displayName);
            _links[peerId] = link;
            // Emit LinkUp synchronously inside the registration critical section:
            // ActiveLinkCount takes the same lock, so no reader can observe the
            // new count before this event lands (the detached session that runs
            // RunSessionAsync would otherwise emit LinkUp after the count is
            // already visible — a lost-event race for callers that check the two
            // together). A replacement re-emits LinkUp here; the superseded
            // session skips its LinkDown.
            RotatingLog.Shared.Info(
                $"linked with peer name={displayName} id={peerId[..Math.Min(8, peerId.Length)]} "
                + $"({(inbound ? "inbound" : "outbound")}) proto_minor={peerVersion.ProtocolMinor}");
            Emit?.Invoke(new LinkUp(peerId, displayName));
        }

        // Session + per-link staleness watchdog run detached.
        _ = Task.Run(() => RunLinkAsync(link, peerId, framed, ct), ct);
    }

    private async Task RunLinkAsync(
        PeerLink link, string peerId, FramedConnection framed, CancellationToken ct)
    {
        using var linkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var ping = Watchdogs.LinkPingLoopAsync(link, _linkPingInterval, linkCts.Token);
        try { await link.RunSessionAsync(linkCts.Token); }
        catch (Exception e) { RotatingLog.Shared.Debug($"link session ended: {e.Message}"); }
        finally
        {
            linkCts.Cancel();
            // Remove AND emit LinkDown only if still the current entry — a
            // replacement already swapped in a new link under this node_id
            // (ReferenceEquals guard). Emitting LinkDown here, under the same lock
            // that guards removal and gated by the same identity check, makes it
            // symmetric with the under-lock LinkUp at registration: a replaced
            // link can never emit LinkDown(X) after its replacement's LinkUp(X)
            // (parity with Python _serve_link + Swift linkClosed).
            lock (_lock)
            {
                if (_links.TryGetValue(peerId, out var cur) && ReferenceEquals(cur, link))
                {
                    _links.Remove(peerId);
                    Emit?.Invoke(new LinkDown(peerId, "peer disconnected"));
                }
            }
            try { await ping; } catch { /* cancelled */ }
            framed.Dispose();
        }
        // Re-admission is handled by the reconnect loop: it re-scans the discovery
        // snapshot every cycle and dials any waiting peer whose slot just freed
        // (worst case one discovery/retry cycle — no new timer).
    }

    private async Task DispatchReceiveAsync(ClipPayload payload, string sourcePeerName)
    {
        await _applyLock.WaitAsync();
        try { if (OnClip is not null) await OnClip(payload, sourcePeerName); }
        finally { _applyLock.Release(); }
    }

    /// Fan out one local clip to every active link. A per-link send failure drops
    /// only that link.
    ///
    /// The per-link gates run here are all keyed on the peer's advertised minor:
    ///  - minor &lt; 1: a kind:"files" clip degrades to its first LOOSE file.
    ///  - minor &lt; 2: a frame over the legacy 16 MiB receive cap is SKIPPED (the
    ///    peer would close the session on it). The link stays UP and the peer
    ///    name lands in SizeSkipped for one aggregated toast.
    ///  - minor 1-2: folder entries are sent AS IS (the peer ignores "path" and
    ///    writes the files flat); logged once per clip per affected link.
    ///  - minor 0 + folder-only clip: nothing to send on that link (log only).
    ///
    /// Both aggregates (OldPeerDrops, SizeSkipped) are per-copy so the caller
    /// emits at most ONE toast of each kind across all peers. Delivered carries
    /// the peers that actually took the clip, so the caller never names a
    /// gate-skipped peer in the "sent" log/toast.
    ///
    /// Each distinct payload variant is encoded at most ONCE per broadcast (and
    /// shares one timestamp): the same bytes back the size gate and every send of
    /// that variant, so an 8-peer mesh never re-encodes the same clip.
    public async Task<BroadcastResult> BroadcastAsync(ClipPayload payload)
    {
        List<PeerLink> targets;
        lock (_lock) targets = _links.Values.ToList();
        int oldPeerDrops = 0;
        var delivered = new List<string>();
        var sizeSkipped = new List<string>();
        double ts = UnixNow();
        // Variant kind ("text"/"image"/"file"/"files") -> its encoded frame, or
        // null when the payload does not fit even the 64 MiB cap.
        var frames = new Dictionary<string, EncodedFrame?>();
        // Evaluated ONCE per clip: does this payload carry folder entries?
        bool hasFolders = payload is FilesClip fcAll && fcAll.Files.Any(f => f.RelPath is not null);

        foreach (var link in targets)
        {
            var toSend = payload;
            int dropped = 0;
            bool flattens = false;
            if (payload is FilesClip fc)
            {
                if (link.PeerProtocolMinor < 1)
                {
                    // Protocol 1.0 takes one kind:"file" frame, which has no
                    // place to carry a path — a folder entry would land loose
                    // and unlabelled. Folder-derived entries are therefore
                    // EXCLUDED from the fallback: a folder-only clip sends
                    // NOTHING on this link (log only, link kept). Loose files
                    // keep the first-file fallback.
                    var loose = fc.Files.FirstOrDefault(f => f.RelPath is null);
                    if (loose is null)
                    {
                        // Reached only when the clip is folder-ONLY: a mixed clip
                        // still has a loose entry for the fallback.
                        if (hasFolders)
                            RotatingLog.Shared.Info(FolderOnlyNoticeMessage(link.PeerName));
                        continue;
                    }
                    dropped = fc.Files.Count - 1;
                    toSend = new FileClip(loose.Name, loose.Data);
                }
                else
                    flattens = hasFolders && link.PeerProtocolMinor < 3;
            }
            if (!frames.TryGetValue(toSend.Kind, out var cached))
            {
                try { cached = WireMessage.Clip(toSend, ts).Encode(); }
                catch (PayloadTooLargeException e)
                {
                    RotatingLog.Shared.Warning($"payload too large, dropping: {e.Message}");
                    cached = null;
                }
                frames[toSend.Kind] = cached;
            }
            if (cached is not { } frame) continue;
            if (!Wire.LinkAcceptsFrame(frame.BodyCount, link.PeerProtocolMinor))
            {
                RotatingLog.Shared.Info(
                    $"clip too large for '{link.PeerName}' (peer protocol < 1.2); skipping");
                sizeSkipped.Add(link.PeerName);
                continue;
            }
            if (!await link.SendEncodedAsync(frame)) continue;
            if (dropped > 0)
                RotatingLog.Shared.Info(
                    $"peer {link.PeerName} protocol minor {link.PeerProtocolMinor} < 1: "
                    + $"sent 1 of {dropped + 1} files");
            // Logged only once the peer actually TOOK the frame: a size-gated or
            // failed link has no flattened tree to warn about.
            if (flattens) RotatingLog.Shared.Info(FlattenNoticeMessage(link.PeerName));
            // Only a DELIVERED downgrade counts toward the fallback toast: a
            // gated or failed link received nothing to leave files behind on.
            oldPeerDrops = Math.Max(oldPeerDrops, dropped);
            delivered.Add(link.PeerName);
        }
        return new BroadcastResult(delivered, oldPeerDrops, sizeSkipped);
    }

    public void Shutdown()
    {
        lock (_lock)
        {
            foreach (var link in _links.Values) link.Dispose();
            _links.Clear();
            // Table cleared under the lock -> each disposed session's RunLinkAsync
            // finally finds no matching entry and stays silent (no LinkDown storm
            // on shutdown), same as the old MarkSuperseded path.
        }
        _listener?.Stop();
        IsServing = false;
    }
}
