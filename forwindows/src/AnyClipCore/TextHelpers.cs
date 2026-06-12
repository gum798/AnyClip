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
    public static string SanitizeFilename(string name)
    {
        int cut = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        var basename = (cut >= 0 ? name[(cut + 1)..] : name).Trim();
        if (basename.Length == 0) return "received.bin";
        var sb = new StringBuilder(basename.Length);
        foreach (var ch in basename)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-' or ' '
                ? ch : '_');
        return sb.ToString();
    }
}
