using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerStatusTests
{
    [Fact]
    public void ZeroPeersKeepsPreMeshText()
    {
        Assert.Equal("Idle", PeerStatus.Line(PeerUiState.Initial));
        var searching = PeerStateReducer.Reduce(PeerUiState.Initial, new PeerDiscovered("n", "a"), 1);
        Assert.Equal("Searching for peer", PeerStatus.Line(searching));
        var err = PeerStateReducer.Reduce(PeerUiState.Initial, new PermissionMissing("network"), 1);
        Assert.Equal("Error: network", PeerStatus.Line(err));
    }

    [Fact]
    public void LinkedListsPeersOrdinalSortedCommaJoined()
    {
        var s = PeerStateReducer.Reduce(PeerUiState.Initial, new LinkUp("id-1", "win-pc"), 1);
        s = PeerStateReducer.Reduce(s, new LinkUp("id-2", "mac-air"), 2);
        Assert.Equal("Linked: mac-air, win-pc", PeerStatus.Line(s)); // sorted by name, ordinal
        s = PeerStateReducer.Reduce(s, new LinkDown("id-1", "x"), 3);
        Assert.Equal("Linked: mac-air", PeerStatus.Line(s));
        s = PeerStateReducer.Reduce(s, new LinkDown("id-2", "peer disconnected"), 4);
        Assert.Equal("Searching for peer", PeerStatus.Line(s));
    }
}
