using Microsoft.Win32;

namespace AnyClip.App;

/// App-local notification preference (HKCU registry — NOT the shared
/// ~/.anyclip/config.json, which other implementations rewrite with only
/// the token). Default: disabled — the tray pulse replaces balloons as
/// the default sync feedback. subKey is injectable for tests.
public sealed class NotificationSettings(string? subKey = null)
{
    public const string DefaultKey = @"Software\AnyClip";
    private const string ValueName = "ShowNotifications";
    private readonly string _subKey = subKey ?? DefaultKey;

    public bool Enabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_subKey);
            return key?.GetValue(ValueName) is int v && v != 0;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(_subKey);
            key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);
        }
    }
}
