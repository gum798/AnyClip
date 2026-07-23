namespace AnyClip.Core;

public abstract record DaemonEvent;
public sealed record PeerDiscovered(string Name, string Addr) : DaemonEvent;
// Stable peer identity on both link events: node_id + display name. node_id is
// a fresh UUID per daemon start, so a peer restart arrives as a new node_id.
public sealed record LinkUp(string NodeId, string PeerName) : DaemonEvent;
public sealed record LinkDown(string NodeId, string Reason) : DaemonEvent;
public sealed record HandshakeFailed(string Addr, string Reason) : DaemonEvent;
public sealed record PermissionMissing(string Kind) : DaemonEvent;

public enum PeerStateKind { Idle, Searching, Linked, Error }

/// UI state is now a peer COLLECTION keyed by node_id -> display name (was a
/// single scalar peer_name). Linked iff Peers is non-empty. Since = first-link
/// timestamp. Port of peer_state.py multi-peer state; parity with Swift
/// PeerUIState and Python peer_state.State.
public sealed record PeerUiState(
    PeerStateKind Kind,
    IReadOnlyDictionary<string, string> Peers,
    double? Since = null,
    string? Reason = null,
    int ConsecutiveHandshakeFails = 0)
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();
    public static readonly PeerUiState Initial = new(PeerStateKind.Idle, Empty);
}

/// Pure reducer — port of peer_state.py. LinkUp inserts/updates by node_id;
/// LinkDown removes ONLY that node_id (never collapses to searching while other
/// peers remain); Kind is Linked iff Peers is non-empty.
public static class PeerStateReducer
{
    public const int HandshakeFailThreshold = 5;

    public static PeerUiState Reduce(PeerUiState prev, DaemonEvent ev, double now) => ev switch
    {
        PermissionMissing p => prev with { Kind = PeerStateKind.Error, Reason = p.Kind },
        LinkUp u => WithPeer(prev, u.NodeId, u.PeerName, now),
        LinkDown d => WithoutPeer(prev, d.NodeId, d.Reason),
        PeerDiscovered when prev.Peers.Count == 0
                         && prev.Kind is PeerStateKind.Idle or PeerStateKind.Error =>
            prev with { Kind = PeerStateKind.Searching },
        PeerDiscovered => prev,
        HandshakeFailed =>
            prev.ConsecutiveHandshakeFails + 1 >= HandshakeFailThreshold
                ? prev with { Kind = PeerStateKind.Error, Reason = "auth",
                              ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 }
                : prev with { ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 },
        _ => prev,
    };

    private static PeerUiState WithPeer(PeerUiState prev, string nodeId, string name, double now)
    {
        var peers = new Dictionary<string, string>(prev.Peers) { [nodeId] = name };
        return prev with
        {
            Kind = PeerStateKind.Linked,
            Peers = peers,
            Since = prev.Peers.Count == 0 ? now : prev.Since, // first link stamps the clock
            Reason = null,
            ConsecutiveHandshakeFails = 0,                     // a live link clears auth backoff
        };
    }

    private static PeerUiState WithoutPeer(PeerUiState prev, string nodeId, string reason)
    {
        if (!prev.Peers.ContainsKey(nodeId)) return prev; // unknown drop: no-op
        var peers = new Dictionary<string, string>(prev.Peers);
        peers.Remove(nodeId);
        return prev with
        {
            Kind = peers.Count > 0 ? PeerStateKind.Linked : PeerStateKind.Searching,
            Peers = peers,
            Reason = peers.Count > 0 ? prev.Reason : reason,
        };
    }
}

/// Tray rendering spec, parity with formacOS MenuIcon: attention (red) whenever
/// not linked; ErrorBang adds the "!" overlay.
public readonly record struct TrayIconSpec(bool Attention, bool ErrorBang)
{
    public static TrayIconSpec For(PeerUiState s) => s.Kind switch
    {
        PeerStateKind.Linked => new TrayIconSpec(false, false),
        PeerStateKind.Error => new TrayIconSpec(true, true),
        _ => new TrayIconSpec(true, false),
    };
}
