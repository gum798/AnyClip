namespace AnyClip.Core;

/// mDNS discovery bookkeeping (knownPeers / addressFails / eventsSeen),
/// platform-neutral so it is testable on macOS. The Windows MdnsBeacon
/// calls IngestAsync from its browse callbacks. Port of the bookkeeping
/// half of anyclip.MdnsBeacon. Thread-safe via lock.
public sealed class PeerDirectory(
    string nodeId,
    Action<DaemonEvent> emit,
    Func<string, int, string, Task> onPeer)
{
    private readonly object _lock = new();
    private readonly Dictionary<string, (string Host, int Port, string Label)> _knownPeers = new();
    private readonly Dictionary<string, int> _addressFails = new();
    public int EventsSeen { get; private set; }

    public async Task IngestAsync(string peerId, string host, int port, string label)
    {
        lock (_lock)
        {
            if (peerId == nodeId) return; // self-loopback: no evidence, no record
            EventsSeen++;
            _knownPeers[peerId] = (host, port, label);
            _addressFails.Remove(label);
        }
        RotatingLog.Shared.Info($"discovered peer {label}");
        emit(new PeerDiscovered(label, label));
        await onPeer(host, port, label);
    }

    /// Known peers deduped by address label (a restarted remote daemon
    /// leaves stale node ids behind for the same address).
    public List<(string Host, int Port, string Label)> PeersSnapshot()
    {
        lock (_lock)
        {
            var seen = new HashSet<string>();
            var result = new List<(string, int, string)>();
            foreach (var v in _knownPeers.Values)
                if (seen.Add(v.Label)) result.Add(v);
            return result;
        }
    }

    public int RecordFail(string label)
    {
        lock (_lock)
        {
            var n = _addressFails.GetValueOrDefault(label) + 1;
            _addressFails[label] = n;
            return n;
        }
    }

    public void ClearFails(string label) { lock (_lock) _addressFails.Remove(label); }

    public void PruneAddress(string label)
    {
        lock (_lock)
        {
            foreach (var id in _knownPeers.Where(kv => kv.Value.Label == label)
                                          .Select(kv => kv.Key).ToList())
                _knownPeers.Remove(id);
            _addressFails.Remove(label);
        }
    }
}
