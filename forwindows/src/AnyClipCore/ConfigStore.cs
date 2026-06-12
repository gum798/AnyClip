using System.Text.Json;

namespace AnyClip.Core;

/// Shared ~/.anyclip/config.json ({"token": "..."}), readable/writable by
/// the Python and Swift implementations. Port of config_store.py.
public static class ConfigStore
{
    public static string DefaultDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".anyclip");

    public static string ConfigPath(string? dir = null) =>
        Path.Combine(dir ?? DefaultDir(), "config.json");

    /// 32 random bytes, base64url without padding (secrets.token_urlsafe(32)).
    public static string GenerateToken()
    {
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// null when missing/corrupt/empty — a damaged file never blocks startup.
    public static string? Load(string? dir = null)
    {
        string path = ConfigPath(dir);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("token", out var tok)) return null;
            var token = tok.GetString();
            return string.IsNullOrEmpty(token) ? null : token;
        }
        // UnauthorizedAccessException: an ACL-denied file must not block
        // startup; InvalidOperationException: GetString() on a non-string
        // "token" value.
        catch (Exception e) when (e is JsonException or IOException
            or UnauthorizedAccessException or InvalidOperationException)
        { return null; }
    }

    /// Atomic write: same-dir temp + flush-to-disk + File.Move(overwrite).
    /// chmod 0600 on Unix (no-op on Windows, like Python).
    public static void Save(string token, string? dir = null)
    {
        string targetDir = dir ?? DefaultDir();
        Directory.CreateDirectory(targetDir);
        string target = ConfigPath(targetDir);
        string tmp = Path.Combine(targetDir, $".config.json.{Guid.NewGuid()}.tmp");
        var payload = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["token"] = token },
            new JsonSerializerOptions { WriteIndented = true }) + "\n";
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
            };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var fs = new FileStream(tmp, options))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
                fs.Write(bytes);
                fs.Flush(flushToDisk: true); // fsync, like config_store.py
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { } // best effort; never mask the rethrow
            throw;
        }
    }
}
