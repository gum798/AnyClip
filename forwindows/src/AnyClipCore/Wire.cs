namespace AnyClip.Core;

/// Protocol constants — keep in lockstep with anyclip.py / formacOS.
public static class Wire
{
    public const int MaxPayload = 16 * 1024 * 1024;
    public const int ProtocolMajor = 1;
    public const int ProtocolMinor = 0;
    public const int LegacyVersion = 1;
    public const int DefaultPort = 24816;
    public const string ServiceType = "_anyclip._tcp";
    public const double HandshakeTimeoutSeconds = 5.0;
    public const double ConnectTimeoutSeconds = 5.0;
    public const double RaceWindowSeconds = 1.5;
    public const int MaxReconnectFails = 3;
}
