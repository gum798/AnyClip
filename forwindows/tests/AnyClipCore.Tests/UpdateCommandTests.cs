// forwindows/tests/AnyClipCore.Tests/UpdateCommandTests.cs
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class UpdateCommandTests
{
    private const string Exe = @"C:\Users\u\scoop\apps\anyclip\current\AnyClip.exe";
    private const string Url = "https://example.test/r";
    private const string Log = @"C:\Users\u\.anyclip\update.log";

    [Fact]
    public void HelperBatchHasAllPieces()
    {
        var s = UpdateCommand.WindowsHelperBatch(4242, "scoop", Exe, Url, Log);
        Assert.Contains("PID eq 4242", s);                       // waits for our exit
        Assert.Contains($"start \"\" \"{Exe}\"", s);             // always relaunches
        Assert.Contains($"start \"\" \"{Url}\"", s);             // failure fallback
        Assert.Contains($">> \"{Log}\"", s);                     // evidence trail
    }

    [Fact]
    public void HelperBatchRefreshesBucketsBeforeUpdatingApp()
    {
        // The 1.3.0/1.4.0 tray-update failure: against a stale bucket,
        // `scoop update anyclip` is an exit-0 no-op ("already installed")
        // and the helper relaunches the old version.
        var s = UpdateCommand.WindowsHelperBatch(1, "scoop", Exe, Url, Log);
        int refresh = s.IndexOf("call scoop update >>");
        int app = s.IndexOf("call scoop update anyclip");
        Assert.True(refresh >= 0, "bucket refresh missing");
        Assert.True(app > refresh, "bucket refresh must precede the app update");
    }

    [Fact]
    public void HelperBatchUsesCrlfLineEndings()
    {
        // cmd.exe mis-parses LF-only batch files (labels/goto break).
        var s = UpdateCommand.WindowsHelperBatch(1, "scoop", Exe, Url, Log);
        Assert.DoesNotContain("\n", s.Replace("\r\n", ""));
    }

    [Fact]
    public void ScoopCurrentPathRewritesVersionedDir()
    {
        Assert.Equal(
            @"C:\Users\u\scoop\apps\anyclip\current\AnyClip.exe",
            UpdateCommand.ScoopCurrentPath(
                @"C:\Users\u\scoop\apps\anyclip\1.4.0\AnyClip.exe"));
    }

    [Fact]
    public void ScoopCurrentPathIsCaseInsensitive()
    {
        Assert.Equal(
            @"C:\Users\u\scoop\Apps\AnyClip\current\AnyClip.exe",
            UpdateCommand.ScoopCurrentPath(
                @"C:\Users\u\scoop\Apps\AnyClip\1.4.0\AnyClip.exe"));
    }

    [Fact]
    public void ScoopCurrentPathLeavesCurrentAndForeignPathsAlone()
    {
        Assert.Equal(Exe, UpdateCommand.ScoopCurrentPath(Exe));
        Assert.Equal(
            @"D:\tools\AnyClip\AnyClip.exe",
            UpdateCommand.ScoopCurrentPath(@"D:\tools\AnyClip\AnyClip.exe"));
    }
}
