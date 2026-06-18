// forwindows/src/AnyClipCore/UpdateChecker.cs
using System.Text.Json;

namespace AnyClip.Core;

public abstract record UpdateStatus
{
    public sealed record UpToDate(string Current) : UpdateStatus;
    public sealed record Available(string Latest, string Url) : UpdateStatus;
    public sealed record Failed(string Reason) : UpdateStatus;
}

/// Pure update detection. Network IO is injected via `fetch` so the
/// parse/compare logic is unit-testable without hitting GitHub.
public static class UpdateChecker
{
    public const string ReleasesApiUrl =
        "https://api.github.com/repos/gum798/AnyClip/releases/latest";
    public const string ReleasesPageUrl =
        "https://github.com/gum798/AnyClip/releases/latest";

    public static string? ParseLatestTag(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var t)
                && t.ValueKind == JsonValueKind.String
                && t.GetString() is { } tag)
            {
                var v = tag.StartsWith('v') ? tag[1..] : tag;
                return string.IsNullOrEmpty(v) ? null : v;
            }
        }
        catch (JsonException) { /* fall through */ }
        return null;
    }

    /// Semver-ish: numeric components dominate; a pre-release ("-" suffix)
    /// ranks below the same core; non-numeric components sort low. Returns
    /// negative / 0 / positive like a comparator.
    public static int CompareVersions(string a, string b)
    {
        var (na, preA) = Parse(a);
        var (nb, preB) = Parse(b);
        int n = Math.Max(na.Count, nb.Count);
        for (int i = 0; i < n; i++)
        {
            int x = i < na.Count ? na[i] : 0;
            int y = i < nb.Count ? nb[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        if (preA != preB) return preA ? -1 : 1;
        return 0;
    }

    private static (List<int> nums, bool isPre) Parse(string v)
    {
        var core = v.Split('-', 2)[0];
        var nums = core.Split('.')
            .Select(s => int.TryParse(s, out var n) ? n : -1).ToList();
        return (nums, v.Contains('-'));
    }

    public static async Task<UpdateStatus> CheckForUpdateAsync(
        string current, Func<Task<string>> fetch)
    {
        string body;
        try { body = await fetch(); }
        catch (Exception e) { return new UpdateStatus.Failed(e.Message); }
        var latest = ParseLatestTag(body);
        if (latest is null) return new UpdateStatus.Failed("could not parse latest release");
        return CompareVersions(current, latest) < 0
            ? new UpdateStatus.Available(latest, ReleasesPageUrl)
            : new UpdateStatus.UpToDate(current);
    }
}
