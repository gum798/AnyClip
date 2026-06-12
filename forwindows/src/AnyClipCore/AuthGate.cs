namespace AnyClip.Core;

/// Per-IP cooldown after repeated handshake failures (5 fails → 60 s).
/// Port of anyclip.AuthGate with the Swift-port fix: RecordFail sweeps
/// expired entries BEFORE reading the old count, so a stale count never
/// carries into a new window. Caller provides synchronization.
public sealed class AuthGate(Func<double>? now = null)
{
    public const int MaxFails = 5;
    public const double CooldownSeconds = 60.0;

    private readonly Func<double> _now =
        now ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
    private readonly Dictionary<string, (int Count, double Last)> _fails = new();

    public bool IsBlocked(string ip)
    {
        if (!_fails.TryGetValue(ip, out var e)) return false;
        if (_now() - e.Last >= CooldownSeconds) return false; // expired never blocks
        return e.Count >= MaxFails;
    }

    public void RecordFail(string ip)
    {
        Sweep();
        var count = _fails.TryGetValue(ip, out var e) ? e.Count : 0;
        _fails[ip] = (count + 1, _now());
    }

    public void RecordOk(string ip) => _fails.Remove(ip);

    private void Sweep()
    {
        var t = _now();
        foreach (var ip in _fails.Where(kv => t - kv.Value.Last >= CooldownSeconds)
                                 .Select(kv => kv.Key).ToList())
            _fails.Remove(ip);
    }
}
