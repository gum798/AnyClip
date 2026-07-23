namespace AnyClip.Core;

/// Human status line for the tray/menu. Zero peers keeps the pre-mesh
/// presentation; >=1 peer -> "Linked: " + display names sorted ordinally,
/// comma-joined. Keep in lockstep with the Swift StatusItemController and the
/// Python app shells.
public static class PeerStatus
{
    public static string Line(PeerUiState s)
    {
        if (s.Peers.Count > 0)
            return "Linked: " + string.Join(", ",
                s.Peers.Values.OrderBy(n => n, StringComparer.Ordinal));
        return s.Kind switch
        {
            PeerStateKind.Searching => "Searching for peer",
            PeerStateKind.Error => $"Error: {s.Reason ?? "unknown"}",
            _ => "Idle",
        };
    }
}
