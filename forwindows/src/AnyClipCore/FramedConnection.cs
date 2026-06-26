using System.Net.Sockets;

namespace AnyClip.Core;

/// Raised when an app-initiated send parks past Wire.SendTimeoutSeconds. The
/// connection is dropped so the next recv throws and the reconnect loop runs.
public sealed class SendTimeoutException() : Exception("send timed out (link wedged)");

/// Async framing over one connected Socket: 4-byte BE length + JSON body.
/// Port of PeerLink._send/_recv. EOF/socket errors throw (the session loop
/// treats any throw as end-of-session); invalid frames return null.
public sealed class FramedConnection : IDisposable
{
    private readonly Socket? _socket;
    private readonly Stream _stream;
    public string? RemoteIp { get; }

    /// Per-connection send budget; an instance hook so tests can shrink it.
    internal TimeSpan SendTimeout { get; set; } =
        TimeSpan.FromSeconds(Wire.SendTimeoutSeconds);

    public FramedConnection(Socket socket)
    {
        _socket = socket;
        EnableKeepalive(socket);
        _stream = new NetworkStream(socket, ownsSocket: true);
        RemoteIp = (socket.RemoteEndPoint as System.Net.IPEndPoint)?
            .Address.ToString();
    }

    /// Test seam: drive the framing layer over any stream (no socket).
    internal FramedConnection(Stream stream, string? remoteIp = null)
    {
        _socket = null;
        _stream = stream;
        RemoteIp = remoteIp;
    }

    public static async Task<FramedConnection> ConnectAsync(
        string host, int port, double timeoutSeconds, CancellationToken ct)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await socket.ConnectAsync(host, port, timeoutCts.Token);
            return new FramedConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// TCP keepalive, idle 15 s — same tuning as the Python/Swift ports.
    private static void EnableKeepalive(Socket socket)
    {
        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            // Bound the probe count so the OS actually tears a dead peer down
            // (~15s idle + 4×5s ≈ 35s) as a backstop to the app-layer heartbeat,
            // instead of probing indefinitely.
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 4);
        }
        catch (SocketException) { /* best effort, like the other ports */ }
    }

    public async Task SendFrameAsync(WireMessage message, CancellationToken ct)
    {
        byte[] frame = message.EncodeFrame(); // PayloadTooLargeException propagates
        var write = _stream.WriteAsync(frame, ct).AsTask();
        try
        {
            await write.WaitAsync(SendTimeout, ct);
        }
        catch (TimeoutException)
        {
            // The write parked past the budget -- half-open/wedged socket, or a
            // lost send completion. Abort the connection so the next recv throws
            // and the reconnect loop runs; never let a stuck send freeze the
            // caller's loop (clipboard poll loop / heartbeat self-heal).
            RotatingLog.Shared.Info(
                "send timed out (link wedged); dropping link to force reconnect");
            Dispose();
            // Observe the abandoned write's eventual fault so it doesn't surface
            // as an UnobservedTaskException.
            _ = write.ContinueWith(static t => { _ = t.Exception; },
                TaskScheduler.Default);
            throw new SendTimeoutException();
        }
    }

    public async Task<WireMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var header = new byte[4];
        await _stream.ReadExactlyAsync(header, ct); // EndOfStreamException on EOF
        int n = WireMessage.FrameLength(header);
        if (n <= 0 || n > Wire.MaxPayload)
        {
            RotatingLog.Shared.Warning($"invalid frame length: {n}");
            return null;
        }
        var body = new byte[n];
        await _stream.ReadExactlyAsync(body, ct);
        var msg = WireMessage.DecodeBody(body);
        if (msg is null) RotatingLog.Shared.Warning($"bad json frame ({n} bytes)");
        return msg;
    }

    public void Dispose()
    {
        try { _stream.Dispose(); } catch (IOException) { }
        try { _socket?.Dispose(); } catch (ObjectDisposedException) { }
    }
}
