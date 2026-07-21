using System.Text;

namespace AnyClip.Core;

public static class TextHelpers
{
    /// One-line toast preview. Port of anyclip.preview().
    public static string Preview(string text, int maxLen = 80)
    {
        var snippet = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (snippet.Length == 0) return "(empty)";
        return snippet.Length <= maxLen ? snippet : snippet[..maxLen] + "...";
    }

    /// Basename (split on '/' and '\\', never ':') then replace anything
    /// outside [unicode-alnum . _ - space] with '_'. Port of the Python
    /// sanitizer in ClipboardWatcher.update_local_file.
    /// NFC-normalize, tolerating ill-formed UTF-16. String.Normalize throws
    /// ArgumentException on unpaired surrogates, which NTFS permits in file
    /// names; falling back to the raw string keeps a send from silently
    /// dropping the file (or a receive from tearing down the link).
    public static string ToNfc(string s)
    {
        try { return s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// Cross-platform denylist sanitizer (receive side). Keep in lockstep with
    /// Swift sanitizeFilename and anyclip.update_local_file:
    /// NFC; basename; replace \ / < > : " | ? *, controls (< U+0020), U+007F;
    /// trim trailing dots/spaces; empty/./.. -> received.bin; Windows reserved
    /// device names (stem before first dot, case-insensitive) -> "_" prefix.
    public static string SanitizeFilename(string name)
    {
        name = ToNfc(name);
        int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var basename = cut >= 0 ? name[(cut + 1)..] : name;
        var sb = new StringBuilder(basename.Length);
        foreach (var ch in basename)
            sb.Append(
                ch is '\\' or '/' or '<' or '>' or ':' or '"' or '|' or '?' or '*'
                    || ch < ' ' || ch == ''
                ? '_' : ch);
        var cleaned = sb.ToString().TrimEnd('.', ' ');
        if (cleaned.Length == 0 || cleaned == "." || cleaned == "..") return "received.bin";
        int dot = cleaned.IndexOf('.');
        var stem = dot >= 0 ? cleaned[..dot] : cleaned;
        if (ReservedDeviceNames.Contains(stem)) cleaned = "_" + cleaned;
        return cleaned;
    }

    /// De-duplicate already-sanitized names within one received batch:
    /// first wins, later dupes get " (2)", " (3)" before the LAST extension
    /// (no extension -> appended). Keep in lockstep with Swift/Python.
    public static IReadOnlyList<string> UniquifyNames(IReadOnlyList<string> names)
    {
        // First occurrence keeps its name; later duplicates get " (2)", " (3)"
        // before the LAST extension (a leading dot is not an extension:
        // ".env" -> ".env (2)"). Candidates colliding with an already-emitted
        // name are bumped further. Lockstep with Swift/Python.
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);
        foreach (var name in names)
        {
            if (used.Add(name)) { result.Add(name); continue; }
            int dot = name.LastIndexOf('.');
            string stem = dot <= 0 ? name : name[..dot];
            string ext = dot <= 0 ? "" : name[dot..];
            int n = 2;
            string candidate = $"{stem} ({n}){ext}";
            while (!used.Add(candidate))
            {
                n++;
                candidate = $"{stem} ({n}){ext}";
            }
            result.Add(candidate);
        }
        return result;
    }
}
