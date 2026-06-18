using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace AnyClip.Core;

/// Raised when the daemon cannot start and retrying will not help.
public sealed class FatalStartupException(string message) : Exception(message);

/// Owns the single active TCP link to a peer; acts as server and client;
/// resolves simultaneous-connect via lexicographic node-id tie-break.
/// Port of anyclip.PeerLink — SemaphoreSlim mirrors the asyncio.Lock and
/// the registration critical section contains no awaits.
public sealed class PeerLink(PeerLink.LinkConfig config, string nodeId)
{
    public sealed record LinkConfig(string Token, int Port, string Name, string AppVersion);

    private readonly string _tokenHash = Hashing.Sha256Hex(config.Token);
    private readonly AuthGate _authGate = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly object _connectingLock = new();
    private readonly HashSet<string> _connecting = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();

    private FramedConnection? _activeConn;
    private string? _peerNodeId;
    // Never-linked sentinel: the Stopwatch epoch is type init (~0), unlike
    // the boot-based monotonic clocks of the Python/Swift ports, so 0.0
    // would treat the first 1.5 s after startup as a simultaneous-connect
    // race even when no link was ever established.
    private double _linkedAt = double.NegativeInfinity;
    // Monotonic timestamp of the last inbound frame on the active link. Drives
    // half-open detection: a peer that slept or vanished without RST/FIN keeps
    // the socket "writable" (our pings never error) yet sends nothing back, so
    // staleness can only be judged from inbound silence, not send failures.
    private double _lastInboundAt = double.NegativeInfinity;
    private TcpListener? _listener;

    public Func<ClipPayload, Task>? OnClip { get; set; }
    public Action<DaemonEvent>? Emit { get; set; }
    public volatile bool IsServing;
    public string? PeerName { get; private set; }
    public bool IsActive => _activeConn is not null;

    private static double MonotonicNow() => Clock.Elapsed.TotalSeconds;
    private static double UnixNow() =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public async Task ServeAsync(CancellationToken ct)
    {
        if (_listener is not null)
            throw new FatalStartupException("ServeAsync called twice on the same PeerLink");

        TcpListener? listener = null;
        for (int attempt = 0; ; attempt++)
        {
            listener = new TcpListener(IPAddress.Any, config.Port);
            // POSIX-only SO_REUSEADDR (parity: asyncio.start_server sets it
            // on POSIX; formacOS sets allowLocalEndpointReuse) so rebinding
            // over TIME_WAIT works between runs. Skipped on Windows, where
            // rebind over TIME_WAIT is already allowed and SO_REUSEADDR has
            // port-hijack semantics. Does NOT defeat the bind-retry test:
            // SO_REUSEADDR never permits binding over an active LISTEN.
            if (!OperatingSystem.IsWindows())
                listener.Server.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            try
            {
                listener.Start();
                break;
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                listener.Stop();
                if (attempt >= 4)
                    throw new FatalStartupException(
                        $"port {config.Port} still in use after cleanup attempt; "
                        + "another process may have grabbed it");
                RotatingLog.Shared.Info(
                    $"tcp/{config.Port} still in use; retrying bind ({attempt + 1}/4)");
                await Task.Delay(500, ct);
            }
        }
        _listener = listener;
        IsServing = true;
        RotatingLog.Shared.Info($"listening on tcp/{config.Port}");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // OperationCanceledException from ct keeps propagating so
                // the task surfaces as Canceled to Daemon.RunOnceAsync.
                Socket socket;
                try { socket = await listener.AcceptSocketAsync(ct); }
                catch (SocketException e) when (e.SocketErrorCode != SocketError.OperationAborted)
                { continue; }                              // transient accept failure; listener still up
                catch (Exception e) when (
                    e is SocketException or ObjectDisposedException or InvalidOperationException)
                {
                    // The listen socket was aborted/disposed. If we were NOT asked
                    // to shut down, the OS killed it out from under us (sleep/resume
                    // or a NIC reset), not a clean Stop()/Shutdown(). Returning
                    // normally here would let the supervisor's WhenAny see a
                    // RanToCompletion serve task and silently exit with tcp/24816
                    // unbound (the Windows wedge). Throw the restart sentinel so the
                    // supervisor rebinds — matching macOS/Python, whose serve() can
                    // only exit via cancellation or a thrown restart.
                    if (!ct.IsCancellationRequested)
                        throw new DaemonRestartException(
                            $"listener accept aborted ({e.GetType().Name}); rebinding daemon "
                            + "(likely sleep/resume or network change)");
                    break;                                 // genuine shutdown
                }
                _ = Task.Run(() => HandleInboundAsync(socket, ct), ct);
            }
        }
        finally
        {
            listener.Stop();
            _listener = null;
            IsServing = false;
        }
    }

    private async Task HandleInboundAsync(Socket socket, CancellationToken ct)
    {
        FramedConnection framed;
        try { framed = new FramedConnection(socket); }
        catch (SocketException) { socket.Dispose(); return; }
        RotatingLog.Shared.Debug($"inbound from {framed.RemoteIp ?? "?"}");
        bool blocked;
        await _lock.WaitAsync(ct);
        try { blocked = framed.RemoteIp is not null && _authGate.IsBlocked(framed.RemoteIp); }
        finally { _lock.Release(); }
        if (blocked)
        {
            RotatingLog.Shared.Info(
                $"auth gate: {framed.RemoteIp} blocked (>{AuthGate.MaxFails} failures, "
                + $"cooldown {(int)AuthGate.CooldownSeconds}s)");
            framed.Dispose();
            return;
        }
        // try/catch/finally mirrors anyclip.py _handle_inbound
        // (anyclip.py:1147-1152): the socket always closes, and the
        // fire-and-forget Task.Run in ServeAsync never faults unobserved.
        try { await SessionAsync(framed, inbound: true, ct); }
        catch (Exception e)
        { RotatingLog.Shared.Debug($"inbound session ended: {e.Message}"); }
        finally { framed.Dispose(); }
    }

    public async Task TryConnectAsync(string host, int port, string label, CancellationToken ct)
    {
        if (IsActive) return;
        lock (_connectingLock)
        {
            if (!_connecting.Add(label))
            {
                RotatingLog.Shared.Debug($"connect to {label} already in flight, skipping");
                return;
            }
        }
        try
        {
            FramedConnection framed;
            try
            {
                framed = await FramedConnection.ConnectAsync(
                    host, port, Wire.ConnectTimeoutSeconds, ct);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                RotatingLog.Shared.Info($"connect to {label} failed: {e.Message}");
                return;
            }
            RotatingLog.Shared.Debug($"outbound connected to {label}");
            // Mirrors anyclip.py try_connect (anyclip.py:1172-1179): a
            // propagated session exception must never kill the Task 7
            // reconnect loop awaiting this method.
            try { await SessionAsync(framed, inbound: false, ct); }
            catch (Exception e)
            { RotatingLog.Shared.Debug($"outbound session ended: {e.Message}"); }
            finally { framed.Dispose(); }
        }
        finally
        {
            lock (_connectingLock) _connecting.Remove(label);
        }
    }

    private async Task SessionAsync(FramedConnection framed, bool inbound, CancellationToken ct)
    {
        try
        {
            await framed.SendFrameAsync(WireMessage.Hello(
                _tokenHash, nodeId, config.Name, config.AppVersion), ct);
        }
        catch { return; }
        string addr = framed.RemoteIp ?? "";

        WireMessage? hello;
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(TimeSpan.FromSeconds(Wire.HandshakeTimeoutSeconds));
            hello = await framed.ReceiveMessageAsync(hsCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RotatingLog.Shared.Warning("handshake timeout");
            Emit?.Invoke(new HandshakeFailed(addr, "timeout"));
            return;
        }
        catch { return; }

        if (hello is null || hello.Type != "hello")
        {
            RotatingLog.Shared.Warning("invalid hello, closing");
            Emit?.Invoke(new HandshakeFailed(addr, "invalid"));
            return;
        }
        string? peerIp = inbound ? framed.RemoteIp : null;
        if (hello.Token != _tokenHash)
        {
            RotatingLog.Shared.Warning($"auth failed from peer name={hello.Name ?? "?"}");
            if (peerIp is not null)
            {
                await _lock.WaitAsync(CancellationToken.None);
                try { _authGate.RecordFail(peerIp); } finally { _lock.Release(); }
            }
            Emit?.Invoke(new HandshakeFailed(peerIp ?? addr, "auth"));
            return;
        }
        var peerVersion = hello.PeerVersionInfo();
        var localVersion = new VersionInfo(
            config.AppVersion, Wire.ProtocolMajor, Wire.ProtocolMinor);
        var compat = VersionNegotiator.Negotiate(localVersion, peerVersion);
        if (!VersionNegotiator.LinkAllowed(compat))
        {
            RotatingLog.Shared.Warning(
                $"version refused: local proto={Wire.ProtocolMajor}.{Wire.ProtocolMinor} "
                + $"vs peer proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor} "
                + $"app={peerVersion.AppVersion} -> {VersionNegotiator.WireValue(compat)}");
            Emit?.Invoke(new HandshakeFailed(
                addr, $"version:{VersionNegotiator.WireValue(compat)}"));
            return;
        }
        if (compat != Compatibility.Compatible)
            RotatingLog.Shared.Info(
                $"version mismatch (link kept): {VersionNegotiator.WireValue(compat)}");
        var peerId = hello.NodeId;
        if (string.IsNullOrEmpty(peerId) || peerId == nodeId)
        {
            RotatingLog.Shared.Debug("self loopback or bad node_id, dropping");
            return;
        }
        if (peerIp is not null)
        {
            await _lock.WaitAsync(CancellationToken.None);
            try { _authGate.RecordOk(peerIp); } finally { _lock.Release(); }
        }

        // Registration / tie-break — no awaits between read and write.
        string displayName = string.IsNullOrEmpty(hello.Name)
            ? peerId[..Math.Min(8, peerId.Length)] : hello.Name!;
        await _lock.WaitAsync(CancellationToken.None);
        try
        {
            if (_activeConn is not null)
            {
                bool race = MonotonicNow() - _linkedAt < Wire.RaceWindowSeconds;
                if (race)
                {
                    bool keepThisLink =
                        (!inbound && string.CompareOrdinal(nodeId, peerId) < 0) ||
                        (inbound && string.CompareOrdinal(nodeId, peerId) > 0);
                    if (!keepThisLink)
                    {
                        RotatingLog.Shared.Debug("tie-breaker: dropping duplicate link (race)");
                        return;
                    }
                    RotatingLog.Shared.Debug("tie-breaker: replacing existing link (race)");
                }
                else
                {
                    RotatingLog.Shared.Info(
                        $"tie-breaker: stale link to {PeerName ?? "?"} replaced by "
                        + $"fresh handshake from {hello.Name ?? "?"}");
                }
                _activeConn.Dispose();
            }
            _activeConn = framed;
            _peerNodeId = peerId;
            PeerName = displayName;
            _linkedAt = MonotonicNow();
            _lastInboundAt = MonotonicNow();
        }
        finally { _lock.Release(); }

        // Everything past registration runs under try/finally, mirroring
        // anyclip.py:1360-1409: even a throwing OnClip/Emit handler must
        // still clear the active link, log, and emit LinkDown — otherwise
        // IsActive stays true forever and the reconnect loops never fire
        // (a zombie link with no recovery path).
        try
        {
            RotatingLog.Shared.Info(
                $"linked with peer name={displayName} id={peerId[..Math.Min(8, peerId.Length)]} "
                + $"({(inbound ? "inbound" : "outbound")}) "
                + $"peer_app_version={peerVersion.AppVersion} "
                + $"peer_proto={peerVersion.ProtocolMajor}.{peerVersion.ProtocolMinor}");
            Emit?.Invoke(new LinkUp(displayName, peerId));

            // Receive loop.
            while (true)
            {
                WireMessage? msg;
                try { msg = await framed.ReceiveMessageAsync(ct); }
                catch { break; }
                // Any inbound frame proves the peer is alive; refresh the
                // liveness clock for the heartbeat deadline.
                if (ReferenceEquals(_activeConn, framed)) _lastInboundAt = MonotonicNow();
                if (msg is null) break;
                switch (msg.Type)
                {
                    case "clip":
                        await HandleClipAsync(msg);
                        break;
                    case "ping":
                        try { await framed.SendFrameAsync(WireMessage.Pong(UnixNow()), ct); }
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
            bool wasActive;
            await _lock.WaitAsync(CancellationToken.None);
            try
            {
                wasActive = ReferenceEquals(_activeConn, framed);
                if (wasActive)
                {
                    _activeConn = null;
                    _peerNodeId = null;
                    PeerName = null;
                }
            }
            finally { _lock.Release(); }
            RotatingLog.Shared.Info("peer disconnected");
            if (wasActive) Emit?.Invoke(new LinkDown("peer disconnected"));
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
            default:
                RotatingLog.Shared.Debug($"ignoring clip with kind={kind}");
                break;
        }
    }

    public async Task SendClipAsync(ClipPayload payload)
    {
        var conn = _activeConn;
        if (conn is null) return;
        try { await conn.SendFrameAsync(WireMessage.Clip(payload, UnixNow()), CancellationToken.None); }
        catch (PayloadTooLargeException e)
        { RotatingLog.Shared.Warning($"payload too large, dropping: {e.Message}"); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
    }

    public async Task SendPingAsync()
    {
        var conn = _activeConn;
        if (conn is null) return;
        try { await conn.SendFrameAsync(WireMessage.Ping(UnixNow()), CancellationToken.None); }
        catch (Exception e)
        { RotatingLog.Shared.Info($"send failed (link likely down): {e.Message}"); }
    }

    /// Seconds since the last inbound frame on the active link, or null if not
    /// linked. The heartbeat loop compares this against its deadline.
    public double? SecondsSinceInbound()
        => _activeConn is null ? null : MonotonicNow() - _lastInboundAt;

    /// Drop a link that has gone silent — half-open socket: the peer slept or
    /// vanished without RST/FIN, so our sends never error and the parked
    /// receive never wakes. Disposing the connection wakes that receive, the
    /// session loop tears down (clearing the active link) and the reconnect
    /// loop takes over. No-op if already unlinked.
    public void DropStaleLink(double idleSeconds)
    {
        var conn = _activeConn;
        if (conn is null) return;
        RotatingLog.Shared.Info(
            $"link to {PeerName ?? "?"} idle {(int)idleSeconds}s with no inbound "
            + "(peer likely asleep / half-open); dropping to force reconnect");
        conn.Dispose();
    }

    public void Shutdown()
    {
        _activeConn?.Dispose();
        _activeConn = null;
        _peerNodeId = null;
        PeerName = null;
        _listener?.Stop();
        IsServing = false;
    }
}
