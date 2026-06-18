// forwindows/tests/AnyClipCore.Tests/UpdateCommandTests.cs
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class UpdateCommandTests
{
    [Fact]
    public void WindowsHelperScriptHasAllPieces()
    {
        var s = UpdateCommand.WindowsHelperScript(
            4242, "scoop", @"C:\apps\AnyClip.exe", "https://example.test/r");
        Assert.Contains("Wait-Process -Id 4242", s);            // waits for our exit
        Assert.Contains("scoop update anyclip", s);
        Assert.Contains(@"Start-Process 'C:\apps\AnyClip.exe'", s); // relaunch on success
        Assert.Contains("Start-Process 'https://example.test/r'", s); // fallback on failure
    }
}
