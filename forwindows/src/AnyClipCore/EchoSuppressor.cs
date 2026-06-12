namespace AnyClip.Core;

/// Last-received hash per kind so the watcher never echoes a peer's update
/// back. Port of anyclip.EchoSuppressor. Caller provides synchronization.
public sealed class EchoSuppressor
{
    private readonly Dictionary<string, string> _last = new();
    public void MarkReceived(string kind, string payloadHash) => _last[kind] = payloadHash;
    public bool ShouldSend(string kind, string payloadHash) =>
        !_last.TryGetValue(kind, out var h) || h != payloadHash;
}
