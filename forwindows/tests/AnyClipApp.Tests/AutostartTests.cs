using AnyClip.App;
using Microsoft.Win32;
using Xunit;

namespace AnyClip.App.Tests;

public class AutostartTests
{
    private static string TestSubKey() =>
        @"Software\AnyClipTest\" + Guid.NewGuid().ToString("N");

    [Fact]
    public void EnableWritesQuotedCommandAndDisableRemoves()
    {
        var subKey = TestSubKey();
        try
        {
            var auto = new Autostart(subKey);
            Assert.False(auto.IsEnabled());
            auto.Enable(@"C:\Program Files\AnyClip\AnyClip.exe");
            Assert.True(auto.IsEnabled());
            using var key = Registry.CurrentUser.OpenSubKey(subKey)!;
            Assert.Equal("\"C:\\Program Files\\AnyClip\\AnyClip.exe\"",
                key.GetValue("AnyClip"));
            auto.Disable();
            Assert.False(auto.IsEnabled());
            auto.Disable(); // idempotent
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(@"Software\AnyClipTest", false); }
    }

    [Fact]
    public void DefaultSubKeyIsTheSharedRunKey()
    {
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run",
            Autostart.DefaultRunKey);
    }
}
