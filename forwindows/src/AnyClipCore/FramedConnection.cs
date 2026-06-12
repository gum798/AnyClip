using System.Net.Sockets;

namespace AnyClip.Core;

/// Async framing over one connected Socket: 4-byte BE length + JSON body.
/// Port of PeerLink._send/_recv. EOF/socket errors throw (the session loop
/// treats any throw as end-of-session); invalid frames return null.
public sealed class FramedConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    public string? RemoteIp { get; }

    public FramedConnection(Socket socket)
    {
        _socket = socket;
        EnableKeepalive(socket);
        _stream = new NetworkStream(socket, ownsSocket: true);
        RemoteIp = (socket.RemoteEndPoint as System.Net.IPEndPoint)?
            .Address.ToString();
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
        }
        catch (SocketException) { /* best effort, like the other ports */ }
    }

    public async Task SendFrameAsync(WireMessage message, CancellationToken ct)
    {
        byte[] frame = message.EncodeFrame(); // PayloadTooLargeException propagates
        await _stream.WriteAsync(frame, ct);
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
        try { _socket.Dispose(); } catch (ObjectDisposedException) { }
    }
}
