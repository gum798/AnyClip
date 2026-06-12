using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class VersionNegotiatorTests
{
    private static VersionInfo V(int major, int minor, string app = "1.0.0") =>
        new(app, major, minor);

    [Fact] public void Same() =>
        Assert.Equal(Compatibility.Compatible, VersionNegotiator.Negotiate(V(1, 0), V(1, 0)));

    [Fact]
    public void PeerOlderMinorLinks()
    {
        var r = VersionNegotiator.Negotiate(V(1, 2), V(1, 0));
        Assert.Equal(Compatibility.PeerOlderMinor, r);
        Assert.True(VersionNegotiator.LinkAllowed(r));
    }

    [Fact]
    public void PeerNewerMinorLinks()
    {
        var r = VersionNegotiator.Negotiate(V(1, 0), V(1, 2));
        Assert.Equal(Compatibility.PeerNewerMinor, r);
        Assert.True(VersionNegotiator.LinkAllowed(r));
    }

    [Fact]
    public void MajorMismatchRefused()
    {
        Assert.False(VersionNegotiator.LinkAllowed(
            VersionNegotiator.Negotiate(V(2, 0), V(1, 5))));
        Assert.False(VersionNegotiator.LinkAllowed(
            VersionNegotiator.Negotiate(V(1, 9), V(2, 0))));
    }

    [Fact]
    public void WireValuesMatchPython()
    {
        Assert.Equal("compatible", VersionNegotiator.WireValue(Compatibility.Compatible));
        Assert.Equal("peer_older_minor", VersionNegotiator.WireValue(Compatibility.PeerOlderMinor));
        Assert.Equal("peer_newer_minor", VersionNegotiator.WireValue(Compatibility.PeerNewerMinor));
        Assert.Equal("peer_older_major", VersionNegotiator.WireValue(Compatibility.PeerOlderMajor));
        Assert.Equal("peer_newer_major", VersionNegotiator.WireValue(Compatibility.PeerNewerMajor));
    }
}
