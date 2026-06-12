namespace AnyClip.App;

/// Balloon-tip notifications over the tray NotifyIcon — the same vehicle
/// the Python build used. Body capped at 240 chars like the other ports.
/// Daemon tasks call Notify off-thread, so the call is marshalled to the
/// UI thread through `Invoker` (same pattern as WinFormsClipboard).
public sealed class Notifier(NotifyIcon trayIcon)
{
    /// UI-thread control set once by Program; until set, calls run inline.
    public Control? Invoker { get; set; }

    public void Notify(string title, string body)
    {
        var inv = Invoker;
        if (inv is { InvokeRequired: true })
        {
            inv.BeginInvoke(() => ShowTip(title, body));
            return;
        }
        ShowTip(title, body);
    }

    private void ShowTip(string title, string body)
    {
        try
        {
            trayIcon.ShowBalloonTip(3000, title,
                body.Length > 240 ? body[..240] : body,
                ToolTipIcon.Info);
        }
        catch (Exception) { /* notifications must never crash the app */ }
    }
}
