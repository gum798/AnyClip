using System.Drawing;
using AnyClip.Core;

namespace AnyClip.App;

/// NotifyIcon + ContextMenuStrip shell. Icon states follow TrayIconSpec:
/// normal when linked; red-tinted when not; red + "!" overlay on error.
public sealed class TrayIcon : IDisposable
{
    public NotifyIcon Notify { get; } = new();
    private readonly ToolStripMenuItem _statusItem = new("Status: Idle") { Enabled = false };
    private readonly ToolStripMenuItem _lastSyncItem = new("Last sync: —") { Enabled = false };
    private readonly ToolStripMenuItem _startAtLoginItem = new("Start at Login");
    private readonly Autostart _autostart = new();
    private readonly Icon _baseIcon;
    private readonly Icon _attentionIcon;
    private readonly Icon _errorIcon;
    private readonly string _logFile;
    private readonly Action _onQuit;

    public TrayIcon(string logFile, Action onQuit)
    {
        _logFile = logFile;
        _onQuit = onQuit;
        _baseIcon = LoadBaseIcon();
        _attentionIcon = Tint(_baseIcon, bang: false);
        _errorIcon = Tint(_baseIcon, bang: true);

        var menu = new ContextMenuStrip();
        var tokenItem = new ToolStripMenuItem("Token…", null, (_, _) => Dialogs.TokenFlow(_onQuit));
        _startAtLoginItem.Checked = _autostart.IsEnabled();
        _startAtLoginItem.Click += (_, _) => ToggleAutostart();
        var openLogsItem = new ToolStripMenuItem("Open Logs", null, (_, _) => OpenLogs());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => _onQuit());

        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tokenItem);
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quitItem);

        Notify.ContextMenuStrip = menu;
        Notify.Visible = true;
        Apply(PeerUiState.Initial);
    }

    /// Red-tinted copy of the base icon, optionally with a "!" overlay.
    /// Internal for the CI render smoke test (InternalsVisibleTo).
    internal static Icon Tint(Icon baseIcon, bool bang)
    {
        using var bmp = baseIcon.ToBitmap();
        using var tinted = new Bitmap(bmp.Width, bmp.Height);
        using (var g = Graphics.FromImage(tinted))
        {
            g.DrawImage(bmp, 0, 0);
            using var overlay = new SolidBrush(Color.FromArgb(112, 220, 40, 40));
            g.FillEllipse(overlay, 0, 0, bmp.Width - 1, bmp.Height - 1);
            if (bang)
            {
                using var font = new Font(FontFamily.GenericSansSerif,
                    bmp.Height * 0.55f, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString("!", font, Brushes.White,
                    new RectangleF(0, 0, bmp.Width, bmp.Height),
                    new StringFormat
                    {
                        Alignment = StringAlignment.Far,
                        LineAlignment = StringAlignment.Far,
                    });
            }
        }
        return Icon.FromHandle(tinted.GetHicon());
    }

    public void Apply(PeerUiState state)
    {
        string status = state.Kind switch
        {
            PeerStateKind.Linked => $"Linked: {state.PeerName ?? "peer"}",
            PeerStateKind.Searching => "Searching for peer",
            PeerStateKind.Error => $"Error: {state.Reason ?? "unknown"}",
            _ => "Idle",
        };
        _statusItem.Text = $"Status: {status}";
        _lastSyncItem.Text = state.Kind == PeerStateKind.Linked
            ? $"Linked since: {DateTime.Now:HH:mm:ss}"
            : "Last sync: —";
        var spec = TrayIconSpec.For(state);
        Notify.Icon = spec switch
        {
            { Attention: false } => _baseIcon,
            { ErrorBang: true } => _errorIcon,
            _ => _attentionIcon,
        };
        // NotifyIcon.Text caps at 127 chars.
        var tip = $"AnyClip — {status}";
        Notify.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    private void ToggleAutostart()
    {
        if (_startAtLoginItem.Checked)
        {
            _autostart.Disable();
            _startAtLoginItem.Checked = false;
            return;
        }
        try
        {
            _autostart.Enable(Environment.ProcessPath ?? Application.ExecutablePath);
            _startAtLoginItem.Checked = true;
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not enable Start at Login:\n{e.Message}",
                "AnyClip", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void OpenLogs()
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_logFile}\"");
        }
        catch (Exception e)
        { RotatingLog.Shared.Warning($"open logs failed: {e.Message}"); }
    }

    /// Single-file publish bundles anyclip.ico into the exe, so the loose
    /// file is absent at runtime; the PE icon (ApplicationIcon) is always
    /// there. Loose-file fallback covers plain `dotnet build` output.
    private static Icon LoadBaseIcon()
    {
        try
        {
            if (Environment.ProcessPath is { } exe
                && Icon.ExtractAssociatedIcon(exe) is { } embedded)
                return embedded;
        }
        catch (Exception) { /* fall through to loose file */ }
        return new Icon(Path.Combine(AppContext.BaseDirectory, "anyclip.ico"));
    }

    public void Dispose()
    {
        Notify.Visible = false;
        Notify.Dispose();
    }
}
