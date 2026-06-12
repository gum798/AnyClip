using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class PeerDirectoryTests
{
    [Fact]
    public async Task SelfAdvertisementIgnoredWithoutEvidence()
    {
        var dir = new PeerDirectory("self-node", _ => { }, (_, _, _) => Task.CompletedTask);
        await dir.IngestAsync("self-node", "1.2.3.4", 24816, "x");
        Assert.Equal(0, dir.EventsSeen);
        Assert.Empty(dir.PeersSnapshot());
    }

    [Fact]
    public async Task NonSelfPeerRecordedAndOnPeerFired()
    {
        var fired = new List<string>();
        var events = new List<DaemonEvent>();
        var dir = new PeerDirectory("self",
            e => events.Add(e),
            (host, port, label) => { fired.Add($"{host}:{port}:{label}"); return Task.CompletedTask; });
        await dir.IngestAsync("other", "1.2.3.4", 24816, "peer-1");
        Assert.Equal(1, dir.EventsSeen);
        Assert.Single(dir.PeersSnapshot());
        Assert.Equal("peer-1", dir.PeersSnapshot()[0].Label);
        Assert.Contains(events, e => e is PeerDiscovered);
        Assert.Equal(new[] { "1.2.3.4:24816:peer-1" }, fired);
    }

    [Fact]
    public async Task FreshDiscoveryClearsFailCountAndPruneRemovesAllIds()
    {
        var dir = new PeerDirectory("self", _ => { }, (_, _, _) => Task.CompletedTask);
        await dir.IngestAsync("p1", "1.2.3.4", 24816, "addr");
        Assert.Equal(1, dir.RecordFail("addr"));
        Assert.Equal(2, dir.RecordFail("addr"));
        await dir.IngestAsync("p1", "1.2.3.4", 24816, "addr");
        Assert.Equal(1, dir.RecordFail("addr")); // reset by rediscovery

        await dir.IngestAsync("p2", "1.2.3.4", 24816, "addr"); // restarted peer, same addr
        Assert.Single(dir.PeersSnapshot()); // deduped by label
        dir.PruneAddress("addr");
        Assert.Empty(dir.PeersSnapshot());
    }
}
