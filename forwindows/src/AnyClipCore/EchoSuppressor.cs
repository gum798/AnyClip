using System.Diagnostics;

namespace AnyClip.Core;

/// Last-received hash per kind so the watcher never echoes a peer's update
/// back. Port of anyclip.EchoSuppressor. Caller provides synchronization.
///
/// Suppression is bounded to SuppressWindowSeconds after the receive: the
/// mechanical echo (our own clipboard write re-observed) always lands within
/// seconds — and the watcher pre-filters it anyway (content baselines here and
/// in Python, changeCount in Swift), so this class is the backstop for races.
/// Without the window, a user DELIBERATELY re-copying the exact string they
/// last received could never send it back (it hashes identically to the echo),
/// for as long as no other clip arrived.
public sealed class EchoSuppressor
{
    public const double SuppressWindowSeconds = 30.0;

    private readonly Dictionary<string, (string Hash, double At)> _last = new();

    private static double MonotonicNow() =>
        (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;

    public void MarkReceived(string kind, string payloadHash, double? now = null) =>
        _last[kind] = (payloadHash, now ?? MonotonicNow());

    public bool ShouldSend(string kind, string payloadHash, double? now = null)
    {
        if (!_last.TryGetValue(kind, out var entry) || entry.Hash != payloadHash)
            return true;
        return (now ?? MonotonicNow()) - entry.At > SuppressWindowSeconds;
    }
}
