using AnyClip.App;
using Microsoft.Win32;
using Xunit;

namespace AnyClip.App.Tests;

public class NotificationSettingsTests
{
    [Fact]
    public void DefaultsOffAndRoundTrips()
    {
        var subKey = @"Software\AnyClipTest\" + Guid.NewGuid().ToString("N");
        try
        {
            var settings = new NotificationSettings(subKey);
            Assert.False(settings.Enabled);
            settings.Enabled = true;
            Assert.True(settings.Enabled);
            settings.Enabled = false;
            Assert.False(settings.Enabled);
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AnyClipTest", false); }
    }
}
