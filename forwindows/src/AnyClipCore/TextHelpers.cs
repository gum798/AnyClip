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

    public static string SanitizeFilename(string name)
    {
        // Normalize to NFC first: a macOS peer sends filenames in NFD
        // (decomposed Hangul = conjoining jamo U+11xx Windows can't render).
        // NFC is the cross-platform interchange form. Keep in lockstep with
        // Swift sanitizeFilename and anyclip.update_local_file.
        name = ToNfc(name);
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
