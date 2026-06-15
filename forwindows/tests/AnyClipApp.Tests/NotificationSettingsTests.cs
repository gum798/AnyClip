using AnyClip.App;
using Microsoft.Win32;
using Xunit;

namespace AnyClip.App.Tests;

public class NotificationSettingsTests
{
    [Fact]
    public void DefaultsOffAndRoundTrips()
    {
        // Distinct parent from AutostartTests (which deletes Software\AnyClipTest):
        // xUnit runs test classes in parallel, and tearing down a shared
        // registry parent while the other class operates under it throws
        // "key marked for deletion". A per-class parent removes the race.
        const string parent = @"Software\AnyClipNotifTest";
        var subKey = parent + @"\" + Guid.NewGuid().ToString("N");
        try
        {
            var settings = new NotificationSettings(subKey);
            Assert.False(settings.Enabled);
            settings.Enabled = true;
            Assert.True(settings.Enabled);
            settings.Enabled = false;
            Assert.False(settings.Enabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(parent, false); }
    }
}
