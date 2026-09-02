// forwindows/src/AnyClipCore/UpdateCommand.cs
namespace AnyClip.Core;

/// Pure builders for the detached update helper (kept out of the runtime so
/// the exact commands are unit-testable without spawning anything).
public static class UpdateCommand
{
    /// Rewrites a Scoop versioned install path (...\apps\anyclip\1.4.0\...)
    /// to the `current` junction. Relaunching the path we were running FROM
    /// would resurrect the old build: Scoop keeps superseded version dirs on
    /// disk until `scoop cleanup`. Non-Scoop paths pass through untouched.
    public static string ScoopCurrentPath(string exePath)
    {
        const string marker = @"\apps\anyclip\";
        int i = exePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return exePath;
        int verStart = i + marker.Length;
        int verEnd = exePath.IndexOf('\\', verStart);
        if (verEnd < 0) return exePath;
        string version = exePath[verStart..verEnd];
        return version.Equals("current", StringComparison.OrdinalIgnoreCase)
            ? exePath
            : exePath[..verStart] + "current" + exePath[verEnd..];
    }

    /// Body of the .cmd update helper: wait for `pid` to exit, refresh the
    /// Scoop buckets, update anyclip, relaunch the exe — on failure also open
    /// the releases page. Two hard-won rules are load-bearing here:
    ///
    /// 1. The helper runs in a VISIBLE console. The original hidden
    ///    `powershell.exe -WindowStyle Hidden -Command` helper was killed
    ///    silently (corporate EDR), leaving the app closed, nothing
    ///    relaunched, and no trace. Scoop output also lands in `logPath`.
    /// 2. `scoop update` (bucket refresh) runs BEFORE `scoop update anyclip`.
    ///    Against a stale bucket the app update is an exit-0 no-op
    ///    ("already installed") and the helper relaunches the old version.
    ///
    /// CRLF endings are required — cmd.exe mis-parses LF-only batch files.
    public static string WindowsHelperBatch(
        int pid, string scoopInvocation, string exePath, string releasesUrl,
        string logPath)
    {
        var lines = new[]
        {
            "@echo off",
            "title AnyClip update",
            $"echo Waiting for AnyClip (PID {pid}) to exit...",
            ":wait",
            // The /FI filter guarantees any row printed IS our process; find
            // just distinguishes "row present" from the (localized) no-match
            // INFO message, which contains no digits.
            $"tasklist /FI \"PID eq {pid}\" 2>nul | find \"{pid}\" >nul",
            "if not errorlevel 1 (",
            "  timeout /t 1 /nobreak >nul",
            "  goto wait",
            ")",
            $"echo [%date% %time%] tray update helper started >> \"{logPath}\"",
            "echo Refreshing Scoop buckets...",
            $"call {scoopInvocation} update >> \"{logPath}\" 2>&1",
            "echo Updating AnyClip...",
            $"call {scoopInvocation} update anyclip >> \"{logPath}\" 2>&1",
            "set RC=%errorlevel%",
            $"echo [%date% %time%] scoop update anyclip exit=%RC% >> \"{logPath}\"",
            "if not \"%RC%\"==\"0\" (",
            $"  echo Update failed. Details: {logPath}",
            "  echo Opening the releases page for a manual download...",
            $"  start \"\" \"{releasesUrl}\"",
            "  timeout /t 5 /nobreak >nul",
            ")",
            $"start \"\" \"{exePath}\"",
        };
        return string.Join("\r\n", lines) + "\r\n";
    }
}
