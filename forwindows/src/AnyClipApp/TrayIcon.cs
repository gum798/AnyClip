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
    private readonly ToolStripMenuItem _notificationsItem = new("Notifications");
    private readonly Autostart _autostart = new();
    private readonly Icon _baseIcon;
    private readonly Icon _attentionIcon;
    private readonly Icon _errorIcon;
    private readonly string _logFile;
    private readonly Action _onQuit;
    private readonly NotificationSettings _settings;
    private readonly System.Windows.Forms.Timer _pulseTimer = new() { Interval = 40 };
    private readonly Icon[] _pulseFrames;
    private int _pulseFrame;
    private PeerUiState _currentState = PeerUiState.Initial;
    private readonly ToolStripMenuItem _versionItem;
    private readonly ToolStripMenuItem _checkUpdatesItem = new("Check for Updates");
    private readonly System.Windows.Forms.Timer _updateResetTimer = new() { Interval = 3000 };
    private readonly Func<Task<UpdateStatus>> _checkAsync;
    private readonly Action _installUpdate;
    private readonly Action _openReleases;
    private enum UpdateMode { Idle, Available, Failed }
    private UpdateMode _updateMode = UpdateMode.Idle;
    private string? _availableVersion;

    public TrayIcon(string logFile, NotificationSettings settings, string appVersion,
        Func<Task<UpdateStatus>> checkAsync, Action installUpdate, Action openReleases,
        Action onQuit)
    {
        _logFile = logFile;
        _settings = settings;
        _checkAsync = checkAsync;
        _installUpdate = installUpdate;
        _openReleases = openReleases;
        _onQuit = onQuit;
        _versionItem = new ToolStripMenuItem($"AnyClip v{appVersion}") { Enabled = false };
        _baseIcon = LoadBaseIcon();
        _attentionIcon = Tint(_baseIcon, bang: false);
        _errorIcon = Tint(_baseIcon, bang: true);
        _pulseFrames = BuildPulseFrames(_baseIcon);
        _pulseTimer.Tick += (_, _) =>
        {
            if (_pulseFrame >= _pulseFrames.Length)
            {
                _pulseTimer.Stop();
                Apply(_currentState); // restore the live state icon
                return;
            }
            Notify.Icon = _pulseFrames[_pulseFrame++];
        };
        _updateResetTimer.Tick += (_, _) =>
        {
            _updateResetTimer.Stop();
            if (_updateMode == UpdateMode.Idle) _checkUpdatesItem.Text = "Check for Updates";
        };
        _checkUpdatesItem.Click += async (_, _) => await OnCheckUpdatesClickAsync();

        var menu = new ContextMenuStrip();
        var tokenItem = new ToolStripMenuItem("Token…", null, (_, _) => Dialogs.TokenFlow(_onQuit));
        _startAtLoginItem.Checked = _autostart.IsEnabled();
        _startAtLoginItem.Click += (_, _) => ToggleAutostart();
        _notificationsItem.Checked = _settings.Enabled;
        _notificationsItem.Click += (_, _) =>
        {
            _settings.Enabled = !_settings.Enabled;
            _notificationsItem.Checked = _settings.Enabled;
        };
        var openLogsItem = new ToolStripMenuItem("Open Logs", null, (_, _) => OpenLogs());
        var quitItem = new ToolStripMenuItem("Quit", null, (_, _) => _onQuit());

        menu.Items.Add(_versionItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusItem);
        menu.Items.Add(_lastSyncItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(tokenItem);
        menu.Items.Add(_startAtLoginItem);
        menu.Items.Add(_notificationsItem);
        menu.Items.Add(openLogsItem);
        menu.Items.Add(_checkUpdatesItem);
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
        _currentState = state;
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
        // While the pulse is running, skip the immediate icon update; the
        // animation restores the latest state when it finishes.
        if (!_pulseTimer.Enabled)
        {
            var spec = TrayIconSpec.For(state);
            Notify.Icon = spec switch
            {
                { Attention: false } => _baseIcon,
                { ErrorBang: true } => _errorIcon,
                _ => _attentionIcon,
            };
        }
        // NotifyIcon.Text caps at 127 chars.
        var tip = $"AnyClip — {status}";
        Notify.Text = tip.Length > 127 ? tip[..127] : tip;
    }

    /// 10-frame arc-orbit pulse around the tray icon (~0.4 s). Retrigger
    /// mid-flight restarts the orbit instead of stacking. UI thread only.
    public void AnimateSyncPulse()
    {
        _pulseFrame = 0;
        if (!_pulseTimer.Enabled) _pulseTimer.Start();
    }

    private async Task OnCheckUpdatesClickAsync()
    {
        switch (_updateMode)
        {
            case UpdateMode.Available:
                var ok = MessageBox.Show(
                    "AnyClip will close, update via Scoop, and reopen.",
                    $"Update to v{_availableVersion}?",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
                if (ok == DialogResult.OK) _installUpdate();   // caller spawns helper + quits
                return;
            case UpdateMode.Failed:
                _openReleases();
                _updateMode = UpdateMode.Idle;
                _checkUpdatesItem.Text = "Check for Updates";
                return;
        }
        _checkUpdatesItem.Text = "Checking…";
        _checkUpdatesItem.Enabled = false;
        var status = await _checkAsync();   // resumes on UI thread (WinForms sync context)
        ApplyUpdateStatus(status, silent: false);
    }

    /// Best-effort check on launch: only surface an available update; never
    /// show "up to date"/"failed" and never pop a dialog.
    public async Task RunSilentUpdateCheckAsync()
    {
        var status = await _checkAsync();
        if (status is UpdateStatus.Available) ApplyUpdateStatus(status, silent: true);
    }

    private void ApplyUpdateStatus(UpdateStatus status, bool silent)
    {
        _checkUpdatesItem.Enabled = true;
        switch (status)
        {
            case UpdateStatus.UpToDate u:
                _updateMode = UpdateMode.Idle;
                _availableVersion = null;
                _checkUpdatesItem.Text = $"You're up to date (v{u.Current})";
                _updateResetTimer.Stop();
                _updateResetTimer.Start();
                break;
            case UpdateStatus.Available a:
                _updateMode = UpdateMode.Available;
                _availableVersion = a.Latest;
                _checkUpdatesItem.Text = $"Update to v{a.Latest}";
                break;
            case UpdateStatus.Failed:
                if (silent) return;
                _updateMode = UpdateMode.Failed;
                _availableVersion = null;
                _checkUpdatesItem.Text = "Check failed — open releases";
                break;
        }
    }

    /// Pre-rendered orbit frames: dimmed base icon + a Windows-accent-blue
    /// arc advancing 36°/frame. Built once; HICONs from GetHicon are
    /// explicitly destroyed after cloning so nothing leaks per pulse.
    internal static Icon[] BuildPulseFrames(Icon baseIcon)
    {
        var frames = new Icon[10];
        using var baseBmp = baseIcon.ToBitmap();
        for (int i = 0; i < frames.Length; i++)
        {
            using var bmp = new Bitmap(baseBmp.Width, baseBmp.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var dim = new System.Drawing.Imaging.ColorMatrix
                {
                    Matrix00 = 0.55f, // R
                    Matrix11 = 0.55f, // G
                    Matrix22 = 0.55f, // B
                };
                using var attrs = new System.Drawing.Imaging.ImageAttributes();
                attrs.SetColorMatrix(dim);
                g.DrawImage(baseBmp, new Rectangle(0, 0, bmp.Width, bmp.Height),
                    0, 0, baseBmp.Width, baseBmp.Height, GraphicsUnit.Pixel, attrs);
                using var pen = new Pen(Color.FromArgb(0, 120, 212),
                    Math.Max(2f, bmp.Width / 10f));
                float inset = pen.Width / 2f + 1f;
                g.DrawArc(pen, inset, inset,
                    bmp.Width - 2 * inset, bmp.Height - 2 * inset,
                    i * 36f, 110f);
            }
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                var tmp = Icon.FromHandle(hIcon);
                frames[i] = (Icon)tmp.Clone();
            }
            finally { DestroyIcon(hIcon); }
        }
        return frames;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

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
        _pulseTimer.Stop();
        _pulseTimer.Dispose();
        _updateResetTimer.Stop();
        _updateResetTimer.Dispose();
        Notify.Visible = false;
        Notify.Dispose();
    }
}
