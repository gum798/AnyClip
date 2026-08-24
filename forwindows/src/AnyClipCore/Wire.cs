namespace AnyClip.Core;

/// Protocol constants — keep in lockstep with anyclip.py / formacOS.
public static class Wire
{
    /// 64 MiB hard cap per frame (fits a ~16 MB pptx).
    public const int MaxPayload = 64 * 1024 * 1024;
    /// The receive cap enforced by peers on protocol minor < 2: they CLOSE the
    /// session on a bigger frame, so the broadcast fan-out gates per link on
    /// this value rather than letting an oversize clip tear an old peer's link
    /// down. See LinkAcceptsFrame.
    public const int LegacyMaxPayload = 16 * 1024 * 1024;
    public const int ProtocolMajor = 1;
    /// Cumulative feature level: minor >= 1 accepts kind:"files", minor >= 2
    /// accepts frames up to MaxPayload (64 MiB) instead of LegacyMaxPayload.
    public const int ProtocolMinor = 2;
    public const int LegacyVersion = 1;
    public const int DefaultPort = 24816;
    public const string ServiceType = "_anyclip._tcp";
    public const double HandshakeTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 5.0;
    // BASE upper bound on a single app-initiated send. A write that parks past
    // the budget (full TCP buffer of a half-open/wedged peer) would otherwise
    // freeze the caller's loop -- the clipboard poll loop and the heartbeat
    // self-heal both await sends inline. On timeout the connection is dropped to
    // reconnect. The EFFECTIVE budget scales with the frame; see SendTimeoutFor.
    public const double SendTimeoutSeconds = 10.0;
    public const double RaceWindowSeconds = 1.5;
    public const int MaxReconnectFails = 3;

    /// Drain budget for a frame body of `bytes`: the base timeout plus one
    /// second per MiB (a 1 MiB/s floor). A fixed 10 s could not carry a 64 MiB
    /// frame over a slow LAN, and a timeout drops the connection.
    ///
    /// Invariant: worst case 64 MiB -> 10 + 64 = 74 s, which stays below the
    /// 90 s per-link staleness deadline (Watchdogs.LinkPingLoopAsync: 30 s ping
    /// x dead factor 3), so a legitimately slow big send can never be mistaken
    /// for a half-open link. Keep in lockstep with anyclip.send_timeout_for.
    public static double SendTimeoutFor(int bytes, double baseSeconds = SendTimeoutSeconds)
        => baseSeconds + bytes / (1024.0 * 1024.0);

    /// False when a frame body of `bytes` would breach the legacy 16 MiB receive
    /// cap that a peer on protocol minor < 2 still enforces. Such a peer closes
    /// the session on an over-cap frame, so the fan-out skips the send and KEEPS
    /// the link instead.
    public static bool LinkAcceptsFrame(int bytes, int peerMinor)
        => bytes <= LegacyMaxPayload || peerMinor >= 2;

    /// Receive-side frame-length guard: a body of 1...MaxPayload bytes. Peers on
    /// protocol minor < 2 apply this same rule against LegacyMaxPayload, which is
    /// exactly what LinkAcceptsFrame protects them from.
    public static bool AcceptsFrameLength(int n) => n > 0 && n <= MaxPayload;
}
