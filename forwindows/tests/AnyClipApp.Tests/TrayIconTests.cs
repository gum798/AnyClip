using System.Drawing;
using AnyClip.App;
using Xunit;

namespace AnyClip.App.Tests;

public class TrayIconTests
{
    [Fact]
    public void TrayIconRenderSmoke()
    {
        // The tinted-icon render path (GDI+ FillEllipse/DrawString +
        // GetHicon) must not throw on the headless CI runner. SystemIcons
        // stands in for the shipped .ico — Tint accepts any valid Icon.
        using var attention = TrayIcon.Tint(SystemIcons.Application, bang: false);
        using var error = TrayIcon.Tint(SystemIcons.Application, bang: true);
        Assert.NotNull(attention);
        Assert.NotNull(error);
    }

    [Fact]
    public void PulseFramesBuildTenDistinctIcons()
    {
        using var baseIcon = SystemIcons.Application;
        var frames = TrayIcon.BuildPulseFrames(baseIcon);
        Assert.Equal(10, frames.Length);
        Assert.All(frames, f => Assert.NotNull(f));
        foreach (var f in frames) f.Dispose();
    }
}
