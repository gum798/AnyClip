namespace AnyClip.Core;

public enum Compatibility
{
    Compatible, PeerOlderMinor, PeerNewerMinor, PeerOlderMajor, PeerNewerMajor,
}

/// Port of version_negotiator.py — keep the table in lockstep.
public static class VersionNegotiator
{
    public static Compatibility Negotiate(VersionInfo local, VersionInfo peer)
    {
        if (peer.ProtocolMajor < local.ProtocolMajor) return Compatibility.PeerOlderMajor;
        if (peer.ProtocolMajor > local.ProtocolMajor) return Compatibility.PeerNewerMajor;
        if (peer.ProtocolMinor < local.ProtocolMinor) return Compatibility.PeerOlderMinor;
        if (peer.ProtocolMinor > local.ProtocolMinor) return Compatibility.PeerNewerMinor;
        return Compatibility.Compatible;
    }

    public static bool LinkAllowed(Compatibility c) => c is
        Compatibility.Compatible or
        Compatibility.PeerOlderMinor or
        Compatibility.PeerNewerMinor;

    /// Python enum .value strings (used in HandshakeFailed "version:<x>").
    public static string WireValue(Compatibility c) => c switch
    {
        Compatibility.Compatible => "compatible",
        Compatibility.PeerOlderMinor => "peer_older_minor",
        Compatibility.PeerNewerMinor => "peer_newer_minor",
        Compatibility.PeerOlderMajor => "peer_older_major",
        Compatibility.PeerNewerMajor => "peer_newer_major",
        _ => "unknown",
    };
}
