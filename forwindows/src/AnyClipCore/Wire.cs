namespace AnyClip.Core;

/// Protocol constants — keep in lockstep with anyclip.py / formacOS.
public static class Wire
{
    public const int MaxPayload = 16 * 1024 * 1024;
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 1;
    public const int LegacyVersion = 1;
    public const int DefaultPort = 24816;
    public const string ServiceType = "_anyclip._tcp";
    public const double HandshakeTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 5.0;
    // Upper bound on a single app-initiated send. A write that parks past this
    // (full TCP buffer of a half-open/wedged peer) would otherwise freeze the
    // caller's loop -- the clipboard poll loop and the heartbeat self-heal both
    // await sends inline. On timeout the connection is dropped to reconnect.
    public const double SendTimeoutSeconds = 10.0;
    public const double RaceWindowSeconds = 1.5;
    public const int MaxReconnectFails = 3;
}
