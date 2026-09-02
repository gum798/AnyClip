// forwindows/src/AnyClipApp/UpdateService.cs
using System.Diagnostics;
using AnyClip.Core;

namespace AnyClip.App;

/// Real network + process side of updates. Keeps TrayIcon free of IO.
public sealed class UpdateService(string appVersion, string stateDir)
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
    /// outlives us as a VISIBLE console (see UpdateCommand.WindowsHelperBatch
    /// for why hidden PowerShell was abandoned), refreshes the Scoop buckets,
    /// updates anyclip, and relaunches the exe via the `current` junction.
    public void InstallAndRelaunch()
    {
        try
        {
            string exe = UpdateCommand.ScoopCurrentPath(
                Environment.ProcessPath ?? Application.ExecutablePath);
            string logPath = Path.Combine(stateDir, "update.log");
            string batchPath = Path.Combine(stateDir, "update.cmd");
            File.WriteAllText(batchPath, UpdateCommand.WindowsHelperBatch(
                Environment.ProcessId, "scoop", exe,
                UpdateChecker.ReleasesPageUrl, logPath));
            Process.Start(new ProcessStartInfo(batchPath)
            {
                UseShellExecute = true,
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
