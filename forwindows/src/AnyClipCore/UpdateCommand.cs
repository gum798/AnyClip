// forwindows/src/AnyClipCore/UpdateCommand.cs
namespace AnyClip.Core;

/// Pure builder for the detached PowerShell update helper (kept out of the
/// runtime so the exact command is unit-testable without spawning anything).
public static class UpdateCommand
{
    /// PowerShell -Command body: wait for `pid` to exit, run the scoop
    /// update, relaunch the exe; on failure open the releases page. Uses only
    /// single-quoted literals so it embeds safely in a double-quoted -Command.
    public static string WindowsHelperScript(
        int pid, string scoopInvocation, string exePath, string releasesUrl)
        => $"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
         + $"try {{ {scoopInvocation} update anyclip; Start-Process '{exePath}' }} "
         + $"catch {{ Start-Process '{releasesUrl}' }}";
}
