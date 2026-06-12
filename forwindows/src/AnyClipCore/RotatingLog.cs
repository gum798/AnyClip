namespace AnyClip.Core;

/// Rotating file logger with the Python logging line shape
/// ("yyyy-MM-dd HH:mm:ss,fff LEVEL message"), 5 MB × 3 backups, writing the
/// same ~/.anyclip/anyclip.log. File level is always DEBUG; stderr mirrors
/// INFO+ (DEBUG too when verbose). Thread-safe via lock.
public sealed class RotatingLog(
    string filePath, int maxBytes = 5 * 1024 * 1024,
    int backupCount = 3, bool verbose = false)
{
    private readonly object _lock = new();

    /// Process-wide instance configured by the app entry point; defaults to
    /// a console-only logger so library tests never touch ~/.anyclip.
    public static RotatingLog Shared { get; set; } = new(filePath: "");

    public void Debug(string msg) => Write("DEBUG", msg, console: verbose);
    public void Info(string msg) => Write("INFO", msg, console: true);
    public void Warning(string msg) => Write("WARNING", msg, console: true);
    public void Error(string msg) => Write("ERROR", msg, console: true);

    private void Write(string level, string msg, bool console)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff} {level} {msg}\n";
        lock (_lock)
        {
            if (console) Console.Error.Write(line);
            if (string.IsNullOrEmpty(filePath)) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                // Python RotatingFileHandler rolls over BEFORE emitting when
                // the new record would overflow, so the current file always
                // contains the last written line.
                RotateIfNeeded(System.Text.Encoding.UTF8.GetByteCount(line));
                File.AppendAllText(filePath, line);
            }
            // Widest net (UnauthorizedAccessException etc.), like Python's
            // stdlib handler and Swift's try?.
            catch (Exception) { /* logging must never crash the daemon */ }
        }
    }

    private void RotateIfNeeded(int incomingBytes)
    {
        var info = new FileInfo(filePath);
        // Mirrors RotatingFileHandler.shouldRollover:
        // tell() + len(msg) >= maxBytes.
        if (!info.Exists || info.Length + incomingBytes < maxBytes) return;
        // Plain catches: Delete/Move on read-only/ACL-blocked files throw
        // UnauthorizedAccessException, and rotation must never crash Write.
        try { File.Delete($"{filePath}.{backupCount}"); } catch { }
        for (int i = backupCount - 1; i >= 1; i--)
        {
            var src = $"{filePath}.{i}";
            if (File.Exists(src))
                try { File.Move(src, $"{filePath}.{i + 1}", overwrite: true); }
                catch { }
        }
        try { File.Move(filePath, $"{filePath}.1", overwrite: true); }
        catch { }
    }
}
