// forwindows/src/AnyClipApp/UpdateService.cs
using System.Diagnostics;
using AnyClip.Core;

namespace AnyClip.App;

/// Real network + process side of updates. Keeps TrayIcon free of IO.
public sealed class UpdateService(string appVersion)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    public Task<UpdateStatus> CheckAsync()
        => UpdateChecker.CheckForUpdateAsync(appVersion, FetchLatestJsonAsync);

    private static async Task<string> FetchLatestJsonAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, UpdateChecker.ReleasesApiUrl);
        req.Headers.UserAgent.ParseAdd("AnyClip-updater");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync();
    }

    /// Spawn the detached upgrade helper, then the caller quits. The helper
    /// outlives us, runs `scoop update anyclip`, and relaunches the new exe.
    public void InstallAndRelaunch()
    {
        string exe = (Environment.ProcessPath ?? Application.ExecutablePath).Replace("'", "''");
        string script = UpdateCommand.WindowsHelperScript(
            Environment.ProcessId, "scoop", exe, UpdateChecker.ReleasesPageUrl);
        try
        {
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -WindowStyle Hidden -Command \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception e)
        {
            RotatingLog.Shared.Warning($"update helper spawn failed: {e.Message}");
            OpenReleasesPage();
        }
    }

    public void OpenReleasesPage()
    {
        try
        {
            Process.Start(new ProcessStartInfo(UpdateChecker.ReleasesPageUrl)
            { UseShellExecute = true });
        }
        catch (Exception e)
        { RotatingLog.Shared.Warning($"open releases failed: {e.Message}"); }
    }
}
