using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStateTests
{
    [Fact] public void InitialIsIdleWithNoPeers()
    {
        Assert.Equal(PeerStateKind.Idle, PeerUiState.Initial.Kind);
        Assert.Empty(PeerUiState.Initial.Peers);
    }

    [Fact]
    public void LinkUpAddsPeerKeyedByNodeIdAndGoesLinked()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial,
            new LinkUp("node-abc", "win-pc"), 42.0);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal("win-pc", s.Peers["node-abc"]);
        Assert.Single(s.Peers);
        Assert.Equal(42.0, s.Since);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void SecondLinkUpAddsSecondPeerAndKeepsSince()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("b", "win"), 9);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(2, s.Peers.Count);
        Assert.Equal(1, s.Since); // first-link timestamp retained
    }

    [Fact]
    public void LinkDownRemovesOnlyThatNodeId()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("b", "win"), 2);
        s = PeerStateReducer.Reduce(s, new LinkDown("a", "peer disconnected"), 3);
        Assert.Equal(PeerStateKind.Linked, s.Kind); // still one peer
        Assert.False(s.Peers.ContainsKey("a"));
        Assert.True(s.Peers.ContainsKey("b"));
    }

    [Fact]
    public void LastLinkDownGoesSearchingWithReason()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        s = PeerStateReducer.Reduce(s, new LinkDown("a", "peer disconnected"), 2);
        Assert.Equal(PeerStateKind.Searching, s.Kind);
        Assert.Empty(s.Peers);
        Assert.Equal("peer disconnected", s.Reason);
    }

    [Fact]
    public void UnknownLinkDownIsANoOp()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "mac"), 1);
        var s2 = PeerStateReducer.Reduce(s, new LinkDown("ghost", "x"), 2);
        Assert.Same(s.Peers, s2.Peers); // untouched -> same reference
        Assert.Equal(PeerStateKind.Linked, s2.Kind);
    }

    [Fact]
    public void DiscoveryMovesIdleAndErrorToSearchingOnlyWhenNoPeers()
    {
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1).Kind);
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(PeerStateKind.Searching,
            PeerStateReducer.Reduce(err, new PeerDiscovered("n", "a"), 2).Kind);
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "p"), 1);
        Assert.Same(linked, PeerStateReducer.Reduce(linked, new PeerDiscovered("n", "a"), 2));
    }

    [Fact]
    public void FiveHandshakeFailsTripAuthErrorFromIdle()
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
        Assert.Equal(5, s.ConsecutiveHandshakeFails);
    }

    [Fact]
    public void LinkUpResetsFailCounter()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new HandshakeFailed("a", "auth"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("a", "p"), 2);
        Assert.Equal(PeerStateKind.Linked, s.Kind);
        Assert.Equal(0, s.ConsecutiveHandshakeFails);
        s = PeerStateReducer.Reduce(s, new HandshakeFailed("a", "auth"), 3);
        Assert.NotEqual(PeerStateKind.Error, s.Kind); // still linked, one fail
        Assert.Equal(1, s.ConsecutiveHandshakeFails);
    }

    [Fact] public void ThresholdConstantIsFive() =>
        Assert.Equal(5, PeerStateReducer.HandshakeFailThreshold);

    [Fact]
    public void TrayIconSpecMapping()
    {
        var linked = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("a", "p"), 1);
        Assert.Equal(new TrayIconSpec(false, false), TrayIconSpec.For(linked));
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(PeerUiState.Initial));
        var searching = PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1);
        Assert.Equal(new TrayIconSpec(true, false), TrayIconSpec.For(searching));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("x"), 1);
        Assert.Equal(new TrayIconSpec(true, true), TrayIconSpec.For(err));
    }
}
