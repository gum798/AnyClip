using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStateTests
{
    [Fact] public void InitialIsIdle() =>
        Assert.Equal(PeerStateKind.Idle, PeerUiState.Initial.Kind);

    [Fact]
    public void LinkUpProducesLinked()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial,
            new LinkUp("win-pc", "abc"), 42.0);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal("win-pc", s.PeerName);
        Assert.Equal(42.0, s.Since);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void LinkDownGoesSearching()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        var s = PeerStateReducer.Reduce(linked, new LinkDown("peer disconnected"), 2);
        Assert.Equal(PeerStateKind.Searching, s.Kind);
        Assert.Equal("peer disconnected", s.Reason);
    }

    [Fact]
    public void DiscoveryMovesIdleAndErrorToSearchingOnly()
    {
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1).Kind);
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(err, new PeerDiscovered("n", "a"), 2).Kind);
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        Assert.Equal(linked,
            PeerStateReducer.Reduce(linked, new PeerDiscovered("n", "a"), 2));
    }

    [Fact]
    public void FiveHandshakeFailsTripAuthError()
    {
        var s = PeerUiState.Initial;
        for (int i = 1; i < PeerStateReducer.HandshakeFailThreshold; i++)
        {
            s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), i);
            Assert.Equal(PeerStateKind.Idle, s.Kind);
            Assert.Equal(i, s.ConsecutiveHandshakeFails);
        }
        s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), 5);
        Assert.Equal(PeerStateKind.Error, s.Kind);
        Assert.Equal("auth", s.Reason);
    }

    [Fact]
    public void LinkUpResetsFailCounter()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new HandshakeFailed("a", "auth"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("p", "x"), 2);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    // Tray icon spec — parity with formacOS MenuIcon (red attention when
    // not linked; "!" marker on error).
    [Fact]
    public void TrayIconSpecMapping()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("p", "x"), 1);
        Assert.Equal(new TrayIconSpec(false, false), TrayIconSpec.For(linked));
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(PeerUiState.Initial));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(new TrayIconSpec(true, true), TrayIconSpec.For(err));
    }
}
