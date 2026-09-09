// forwindows/src/AnyClipCore/ClipboardDiagnostics.cs
using System.Runtime.InteropServices;

namespace AnyClip.Core;

/// Wording for clipboard write failures. Pure so the platform-neutral suite
/// pins it; the App layer supplies the owner-process lookup (Win32).
public static class ClipboardDiagnostics
{
    /// HRESULT of CLIPBRD_E_CANT_OPEN: another process holds the clipboard
    /// open (DLP/security agents, RDP clipboard redirection, clipboard
    /// managers). The one failure a retry can outlast.
    public const int ClipboardCantOpenHr = unchecked((int)0x800401D0);

    /// Only a busy clipboard is worth a second attempt: every other failure
    /// (bad payload, non-STA thread, cleared-after-write) is deterministic
    /// and retrying just doubles the stall and the log noise.
    public static bool IsRetryable(Exception e) =>
        (e as ExternalException)?.ErrorCode == ClipboardCantOpenHr;

    public static string DescribeWriteFailure(
        string kind, Exception e, string? ownerProcess)
    {
        string hr = e is ExternalException ee ? $" hr=0x{ee.ErrorCode:X8}" : "";
        string owner = ownerProcess is null
            ? "" : $"; clipboard held by '{ownerProcess}'";
        return $"clipboard write ({kind}) failed: "
            + $"{e.GetType().Name}{hr} {e.Message}{owner}";
    }
}
