using System.Net;
using System.Net.Sockets;
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

/// Protocol 1.2 constants: the 64 MiB frame cap, the legacy 16 MiB receive cap
/// old peers still enforce, the size-scaled send budget, and the boundaries of
/// both guards. Mirrors the constant/timeout half of tests/test_large_frames.py
/// (Python) and Tests/AnyClipCoreTests/LargeFrameTests.swift.
public class LargeFrameTests
{
    // ---- constants ------------------------------------------------------

    [Fact]
    public void FrameCapsAndProtocolMinor()
    {
        Assert.Equal(64 * 1024 * 1024, Wire.MaxPayload);
        Assert.Equal(67108864, Wire.MaxPayload);
        Assert.Equal(16 * 1024 * 1024, Wire.LegacyMaxPayload);
        Assert.Equal(16777216, Wire.LegacyMaxPayload);
        Assert.Equal(2, Wire.ProtocolMinor);
    }

    // ---- send timeout scales with payload -------------------------------

    [Fact]
    public void SendTimeoutScalesAtOneMiBPerSecond()
    {
        Assert.Equal(Wire.SendTimeoutSeconds, Wire.SendTimeoutFor(0));
        Assert.Equal(Wire.SendTimeoutSeconds + 1.0, Wire.SendTimeoutFor(1024 * 1024));
        // Worst case must stay under the 90 s per-link staleness deadline
        // (LinkPingLoopAsync: 30 s ping x dead factor 3).
        Assert.Equal(74.0, Wire.SendTimeoutFor(Wire.MaxPayload));
        Assert.True(Wire.SendTimeoutFor(Wire.MaxPayload) < 30.0 * 3.0);
    }

    [Fact]
    public void SendTimeoutHonoursACustomBase()
    {
        // Tests (and only tests) shrink the base; the scaling still applies.
        Assert.Equal(2.5, Wire.SendTimeoutFor(2 * 1024 * 1024, 0.5));
    }

    // ---- receive guard boundary -----------------------------------------

    [Fact]
    public void RecvGuardAcceptsUpToTheNewCap()
    {
        Assert.True(Wire.AcceptsFrameLength(1));
        Assert.True(Wire.AcceptsFrameLength(Wire.LegacyMaxPayload + 1)); // above the OLD cap
        Assert.True(Wire.AcceptsFrameLength(Wire.MaxPayload));           // exactly at the cap
        Assert.False(Wire.AcceptsFrameLength(Wire.MaxPayload + 1));
        Assert.False(Wire.AcceptsFrameLength(0));
        Assert.False(Wire.AcceptsFrameLength(-1));
    }

    // ---- per-link legacy gate predicate ---------------------------------

    [Fact]
    public void LinkAcceptsFrameGatesOnlyOverCapFramesForOldPeers()
    {
        // At or below the legacy cap every peer takes the frame.
        for (int minor = 0; minor <= 2; minor++)
            Assert.True(Wire.LinkAcceptsFrame(Wire.LegacyMaxPayload, minor));
        // One byte over: only a protocol >= 1.2 peer may receive it.
        Assert.False(Wire.LinkAcceptsFrame(Wire.LegacyMaxPayload + 1, 0));
        Assert.False(Wire.LinkAcceptsFrame(Wire.LegacyMaxPayload + 1, 1));
        Assert.True(Wire.LinkAcceptsFrame(Wire.LegacyMaxPayload + 1, 2));
        Assert.True(Wire.LinkAcceptsFrame(Wire.MaxPayload, 3));
    }

    // ---- encode guard ----------------------------------------------------

    [Fact]
    public void EncodeAcceptsABodyBetweenTheLegacyAndNewCaps()
    {
        // Body lands just over the legacy cap but far under the new one.
        var msg = WireMessage.ClipText(new string('x', Wire.LegacyMaxPayload + 1024), 0);
        var frame = msg.Encode();
        Assert.True(frame.BodyCount > Wire.LegacyMaxPayload);
        Assert.True(frame.BodyCount <= Wire.MaxPayload);
        // BodyCount is the BODY length; the frame carries the 4-byte prefix too.
        Assert.Equal(frame.BodyCount + 4, frame.Bytes.Length);
        Assert.Equal(frame.BodyCount, WireMessage.FrameLength(frame.Bytes[..4]));
    }

    // The over-cap side of the encode guard is `OversizedPayloadThrows` in
    // WireMessageTests, which already asserts against Wire.MaxPayload; it is not
    // repeated here so the suite allocates 64 MiB only once.

    // ---- receive guard boundary over a real socket ----------------------

    [Fact]
    public async Task RecvRejectsAFrameHeaderOverTheNewCap()
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

            // A header claiming one byte more than the cap, plus a tiny body: the
            // guard must reject on the LENGTH alone, without reading the body.
            uint n = (uint)Wire.MaxPayload + 1;
            var head = new byte[]
            {
                (byte)(n >> 24), (byte)(n >> 16), (byte)(n >> 8), (byte)n,
            };
            await rawClient.GetStream().WriteAsync(head);
            await rawClient.GetStream().WriteAsync("{\"type\":\"ping\"}"u8.ToArray());
            Assert.Null(await server.ReceiveMessageAsync(CancellationToken.None));
        }
        finally { listener.Stop(); }
    }
}
