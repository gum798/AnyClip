namespace AnyClip.Core;

public abstract record DaemonEvent;
public sealed record PeerDiscovered(string Name, string Addr) : DaemonEvent;
public sealed record LinkUp(string PeerName, string PeerId) : DaemonEvent;
public sealed record LinkDown(string Reason) : DaemonEvent;
public sealed record HandshakeFailed(string Addr, string Reason) : DaemonEvent;
public sealed record PermissionMissing(string Kind) : DaemonEvent;

public enum PeerStateKind { Idle, Searching, Linked, Error }

public sealed record PeerUiState(
    PeerStateKind Kind,
    string? PeerName = null,
    double? Since = null,
    string? Reason = null,
    int ConsecutiveHandshakeFails = 0)
{
    public static readonly PeerUiState Initial = new(PeerStateKind.Idle);
}

/// Pure reducer — port of peer_state.py.
public static class PeerStateReducer
{
    public const int HandshakeFailThreshold = 5;

    public static PeerUiState Reduce(PeerUiState prev, DaemonEvent ev, double now) => ev switch
    {
        PermissionMissing p => new PeerUiState(PeerStateKind.Error, Reason: p.Kind),
        LinkUp u => new PeerUiState(PeerStateKind.Linked, u.PeerName, now),
        LinkDown d => new PeerUiState(PeerStateKind.Searching, Reason: d.Reason),
        PeerDiscovered when prev.Kind is PeerStateKind.Idle or PeerStateKind.Error =>
            new PeerUiState(PeerStateKind.Searching),
        PeerDiscovered => prev,
        HandshakeFailed =>
            prev.ConsecutiveHandshakeFails + 1 >= HandshakeFailThreshold
                ? new PeerUiState(PeerStateKind.Error, Reason: "auth",
                    ConsecutiveHandshakeFails: prev.ConsecutiveHandshakeFails + 1)
                : prev with { ConsecutiveHandshakeFails = prev.ConsecutiveHandshakeFails + 1 },
        _ => prev,
    };
}

/// Tray rendering spec, parity with formacOS MenuIcon: attention (red)
/// whenever not linked; ErrorBang adds the "!" overlay.
public readonly record struct TrayIconSpec(bool Attention, bool ErrorBang)
{
    public static TrayIconSpec For(PeerUiState s) => s.Kind switch
    {
        PeerStateKind.Linked => new TrayIconSpec(false, false),
        PeerStateKind.Error => new TrayIconSpec(true, true),
        _ => new TrayIconSpec(true, false),
    };
}
