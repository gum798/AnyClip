using System.Diagnostics;
using AnyClip.App;
using Xunit;

namespace AnyClip.App.Tests;

public class PidLockTests
{
    private static string TempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "anyclip-pid-" + Guid.NewGuid());
        Directory.CreateDirectory(d);
        return d;
    }

    [Fact]
    public void PrepareWritesOwnPidAndReleaseRemoves()
    {
        var dir = TempDir();
        var pidLock = new WindowsPidLock(dir);
        pidLock.Prepare(58162);
        Assert.Equal($"{Environment.ProcessId} 58162\n",
            File.ReadAllText(Path.Combine(dir, "anyclip.pid")));
        pidLock.Release();
        Assert.False(File.Exists(Path.Combine(dir, "anyclip.pid")));
    }

    [Fact]
    public void StaleDeadPidIsOverwrittenAndForeignReleaseIgnored()
    {
        var dir = TempDir();
        File.WriteAllText(Path.Combine(dir, "anyclip.pid"), "999999 58162\n");
        var pidLock = new WindowsPidLock(dir);
        pidLock.Prepare(58162);
        Assert.StartsWith($"{Environment.ProcessId} ",
            File.ReadAllText(Path.Combine(dir, "anyclip.pid")));
        // Foreign pid file untouched by Release.
        File.WriteAllText(Path.Combine(dir, "anyclip.pid"), "999999 58162\n");
        pidLock.Release();
        Assert.True(File.Exists(Path.Combine(dir, "anyclip.pid")));
    }

    [Fact]
    public void PrepareKillsLiveProcessFromStalePidFile()
    {
        var dir = TempDir();
        // Real throwaway process that would otherwise live ~30 s.
        var psi = new ProcessStartInfo("cmd", "/c ping -n 30 127.0.0.1 >NUL")
        { CreateNoWindow = true, UseShellExecute = false };
        using var victim = Process.Start(psi)!;
        File.WriteAllText(Path.Combine(dir, "anyclip.pid"), $"{victim.Id} 58162\n");

        var pidLock = new WindowsPidLock(dir);
        pidLock.Prepare(58162);

        Assert.True(victim.WaitForExit(5000)); // Prepare terminated it
        Assert.StartsWith($"{Environment.ProcessId} ",
            File.ReadAllText(Path.Combine(dir, "anyclip.pid")));
        pidLock.Release();
    }
}
