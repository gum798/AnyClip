using Microsoft.Win32;

namespace AnyClip.App;

/// HKCU Run-key autostart — same value name ("AnyClip") and key as the
/// Python build, so a migrating user never has two entries. Port of
/// autostart.WindowsAutostart + format_windows_command.
public sealed class Autostart(string? subKey = null)
{
    public const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "AnyClip";
    private readonly string _subKey = subKey ?? DefaultRunKey;

    public static string FormatCommand(string executablePath) =>
        $"\"{executablePath}\"";

    public bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(ValueName) is not null;
    }

    public void Enable(string executablePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(ValueName, FormatCommand(executablePath));
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
