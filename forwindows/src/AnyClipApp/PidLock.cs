using System.Diagnostics;
using AnyClip.Core;

namespace AnyClip.App;

/// Shared ~/.anyclip/anyclip.pid ("<pid> <port>\n"). Windows semantics
/// follow anyclip.py: the pid-file evidence is trusted (_is_anyclip_pid
/// returns True on win32; no lsof port probe). Kill, wait 2 s, 0.3 s
/// socket settle.
public sealed class WindowsPidLock(string? dir = null) : IPidLock
{
    private readonly string _dir = dir ?? ConfigStore.DefaultDir();
    private string PidFile => Path.Combine(_dir, "anyclip.pid");

    public void Prepare(int port)
    {
        Directory.CreateDirectory(_dir);
        if (File.Exists(PidFile))
        {
            var first = File.ReadAllText(PidFile).Split(' ').FirstOrDefault();
            if (int.TryParse(first, out int oldPid)
                && oldPid > 0 && oldPid != Environment.ProcessId
                && TryGetProcess(oldPid) is { } proc)
            {
                RotatingLog.Shared.Info(
                    $"another anyclip detected (pid {oldPid} via PID file); terminating");
                try
                {
                    proc.Kill();
                    if (!proc.WaitForExit(2000))
                        throw new FatalStartupException(
                            $"could not terminate previous anyclip (pid {oldPid})");
                    RotatingLog.Shared.Info($"previous anyclip (pid {oldPid}) terminated");
                    Thread.Sleep(300); // let the OS release the listen socket
                }
                catch (FatalStartupException) { throw; }
                catch (Exception e)
                {
                    RotatingLog.Shared.Warning($"terminate pid {oldPid} failed: {e.Message}");
                    // Parity with the Python/Swift ports (FatalStartupError):
                    // if the old daemon is still alive we must not start a
                    // second instance against the same port/state dir.
                    using var still = TryGetProcess(oldPid);
                    if (still is not null)
                        throw new FatalStartupException(
                            $"could not terminate previous anyclip (pid {oldPid})");
                }
                finally { proc.Dispose(); }
            }
        }
        try { File.WriteAllText(PidFile, $"{Environment.ProcessId} {port}\n"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { RotatingLog.Shared.Warning($"could not write PID file {PidFile}: {e.Message}"); }
    }

    public void Release()
    {
        try
        {
            if (!File.Exists(PidFile)) return;
            var first = File.ReadAllText(PidFile).Split(' ').FirstOrDefault();
            if (int.TryParse(first, out int pid) && pid == Environment.ProcessId)
                File.Delete(PidFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private static Process? TryGetProcess(int pid)
    {
        try { return Process.GetProcessById(pid); }
        catch (ArgumentException) { return null; } // no such process
    }
}
