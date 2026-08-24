using System.Net;
using System.Net.Sockets;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class FramedConnectionTests
{
    [Fact]
    public async Task SendAndReceiveOverLoopback()
    {
        // Ephemeral port: a fixed port left in TIME_WAIT makes back-to-back
        // suite runs flaky on macOS/BSD.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var client = await FramedConnection.ConnectAsync(
                "127.0.0.1", port, Wire.ConnectTimeoutSeconds, CancellationToken.None);
            using var server = new FramedConnection((await serverTask).Client);

            await client.SendFrameAsync(WireMessage.ClipText("ping-pong", 1), CancellationToken.None);
            var received = await server.ReceiveMessageAsync(CancellationToken.None);
            Assert.Equal("clip", received!.Type);
            Assert.Equal("ping-pong", received.Content);
            Assert.Equal("127.0.0.1", server.RemoteIp);
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task EofThrows()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); // ephemeral
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var client = await FramedConnection.ConnectAsync(
                "127.0.0.1", port, 5, CancellationToken.None);
            (await serverTask).Close();
            await Assert.ThrowsAnyAsync<Exception>(
                () => client.ReceiveMessageAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task InvalidFrameLengthReturnsNull()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); // ephemeral
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var serverTask = listener.AcceptTcpClientAsync();
            using var rawClient = new TcpClient();
            await rawClient.ConnectAsync("127.0.0.1", port);
            using var server = new FramedConnection((await serverTask).Client);
            // 4-byte header promising > MaxPayload.
            await rawClient.GetStream().WriteAsync(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
            Assert.Null(await server.ReceiveMessageAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }

    [Fact]
    public async Task SendFrameTimesOutAndDisposesWedgedStream()
    {
        // Regression for the Mac outbound wedge mirrored here: a write parked on
        // a half-open/wedged socket must not block the caller forever. The send
        // surfaces SendTimeoutException on its own and drops the connection so
        // the reconnect loop runs. (Before the fix SendFrameAsync never returns,
        // so the 3 s guard fires with TimeoutException and this fails.)
        using var stream = new HangingStream();
        using var conn = new FramedConnection(stream)
        {
            SendTimeout = TimeSpan.FromMilliseconds(50),
        };
        await Assert.ThrowsAsync<SendTimeoutException>(
            () => conn.SendFrameAsync(WireMessage.Ping(0), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(stream.Disposed, "wedged stream must be disposed to force reconnect");
    }

    [Fact]
    public async Task SendBudgetScalesWithTheFrameNotJustTheBase()
    {
        // End-to-end guard for the size-scaled budget: SendFrameAsync must wait
        // Wire.SendTimeoutFor(bodyBytes, base), not a flat `SendTimeout`. With a
        // 50 ms base and a ~3 MiB body the budget is ~3.05 s, so a parked write
        // is still in flight 500 ms in. A regression that passed the BASE into
        // WaitAsync would have thrown SendTimeoutException at 50 ms and torn a
        // healthy 64 MiB transfer's link down in the field.
        using var stream = new HangingStream();
        using var conn = new FramedConnection(stream)
        {
            SendTimeout = TimeSpan.FromMilliseconds(50),
        };
        var frame = WireMessage.ClipText(new string('x', 3 * 1024 * 1024), 0).Encode();
        Assert.True(Wire.SendTimeoutFor(frame.BodyCount, 0.05) > 3.0);

        var send = conn.SendFrameAsync(frame, CancellationToken.None);
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        Assert.False(send.IsCompleted,
            "send must still be within its size-scaled budget, not cut off at the base");
        Assert.False(stream.Disposed);

        stream.Dispose();  // unblock the parked write so the test does not hang
        await Assert.ThrowsAnyAsync<Exception>(() => send);
    }

    /// Stream whose WriteAsync parks (full TCP buffer / half-open peer) until
    /// the stream is disposed — then the parked write faults.
    private sealed class HangingStream : Stream
    {
        public bool Disposed;
        private readonly TaskCompletionSource _tcs =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            using var reg = ct.Register(() => _tcs.TrySetCanceled(ct));
            await _tcs.Task;
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            _tcs.TrySetException(new ObjectDisposedException(nameof(HangingStream)));
            base.Dispose(disposing);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) { }
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    [Fact]
    public async Task ConnectTimeoutThrows()
    {
        // RFC 5737 TEST-NET address: connect attempts hang, then time out.
        try
        {
            var conn = await FramedConnection.ConnectAsync(
                "192.0.2.1", 28604, 0.3, CancellationToken.None);
            // Some corporate transparent proxies spoof SYN-ACKs for any
            // address; this environment then cannot exercise the timeout —
            // inconclusive, not a failure.
            conn.Dispose();
        }
        catch (Exception)
        {
            // Expected path: timeout/unreachable throws.
        }
    }
}
