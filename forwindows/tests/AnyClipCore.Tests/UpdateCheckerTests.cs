// forwindows/tests/AnyClipCore.Tests/UpdateCheckerTests.cs
using AnyClip.Core;
using Xunit;

namespace AnyClip.Core.Tests;

public class UpdateCheckerTests
{
    [Fact]
    public void CompareVersionsOrders()
    {
        Assert.True(UpdateChecker.CompareVersions("1.1.6", "1.1.7") < 0);
        Assert.Equal(0, UpdateChecker.CompareVersions("1.1.7", "1.1.7"));
        Assert.True(UpdateChecker.CompareVersions("1.2.0", "1.1.9") > 0);
        Assert.True(UpdateChecker.CompareVersions("1.1.10", "1.1.9") > 0);   // numeric
        Assert.True(UpdateChecker.CompareVersions("1.1.8-beta", "1.1.8") < 0); // pre-release lower
        Assert.True(UpdateChecker.CompareVersions("0.0.0-dev", "1.1.7") < 0);  // dev lowest
    }

    [Fact]
    public void ParseLatestTagStripsV()
    {
        Assert.Equal("1.1.7", UpdateChecker.ParseLatestTag("{\"tag_name\":\"v1.1.7\"}"));
        Assert.Equal("1.2.0", UpdateChecker.ParseLatestTag("{\"tag_name\":\"1.2.0\"}"));
        Assert.Null(UpdateChecker.ParseLatestTag("{\"no_tag\":true}"));
        Assert.Null(UpdateChecker.ParseLatestTag("not json"));
    }

    [Fact]
    public async Task CheckForUpdateClassifies()
    {
        var newer = await UpdateChecker.CheckForUpdateAsync("1.1.6",
            () => Task.FromResult("{\"tag_name\":\"v1.1.7\"}"));
        var a = Assert.IsType<UpdateStatus.Available>(newer);
        Assert.Equal("1.1.7", a.Latest);
        Assert.Equal(UpdateChecker.ReleasesPageUrl, a.Url);

        var same = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromResult("{\"tag_name\":\"v1.1.7\"}"));
        Assert.IsType<UpdateStatus.UpToDate>(same);

        var bad = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromResult("garbage"));
        Assert.IsType<UpdateStatus.Failed>(bad);

        var threw = await UpdateChecker.CheckForUpdateAsync("1.1.7",
            () => Task.FromException<string>(new Exception("boom")));
        Assert.IsType<UpdateStatus.Failed>(threw);
    }
}
