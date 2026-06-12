using AnyClip.Core;

namespace AnyClip.App;

/// Onboarding + token dialogs (WinForms). Mirrors onboarding_win.py's
/// three-way flow and the macOS port's Token… Close/Enter/Reset flow.
public static class Dialogs
{
    /// env > config.json > onboarding dialog. null = user cancelled.
    public static string? ResolveToken()
    {
        var env = Environment.GetEnvironmentVariable("ANYCLIP_TOKEN");
        if (!string.IsNullOrEmpty(env)) return env;
        if (ConfigStore.Load() is { } stored) return stored;
        var token = ShowOnboarding();
        if (token is not null) TrySave(token);
        return token;
    }

    private static string? ShowOnboarding()
    {
        using var form = BuildChoiceForm(
            "Welcome to AnyClip",
            "Choose how to set the shared clipboard token.\n"
            + "Both devices must use the same value.",
            "Generate new token (first device)",
            "Enter existing token (second device)");
        return form.ShowDialog() switch
        {
            DialogResult.Yes => ConfigStore.GenerateToken(),
            DialogResult.No => PromptForToken(),
            _ => null,
        };
    }

    private static string? PromptForToken()
    {
        using var form = new Form
        {
            Text = "Enter shared token",
            Width = 420, Height = 150,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, TopMost = true,
        };
        var field = new TextBox { Left = 12, Top = 12, Width = 380 };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Left = 226, Top = 50, Width = 80 };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Left = 312, Top = 50, Width = 80 };
        form.Controls.AddRange(new Control[] { field, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        if (form.ShowDialog() != DialogResult.OK) return null;
        var value = field.Text.Trim();
        return value.Length == 0 ? null : value;
    }

    /// Token… menu flow: show current + (Close default / Enter / Reset).
    public static void TokenFlow(Action quit)
    {
        var current = ConfigStore.Load() ?? "(no token configured)";
        using var form = BuildChoiceForm(
            "AnyClip token",
            $"Current token:\n{current}\n\nStored at: {ConfigStore.ConfigPath()}\n\n"
            + "Enter token… lets you paste the token from your other device.\n"
            + "Reset… generates a new random token.",
            "Enter token…", "Reset…", closeIsDefault: true);
        switch (form.ShowDialog())
        {
            case DialogResult.Yes: // Enter token…
                if (PromptForToken() is { } entered && TrySave(entered))
                {
                    MessageBox.Show(
                        "AnyClip will now quit. Relaunch to apply, then make "
                        + "sure your other device uses the same token.",
                        "Token saved", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    quit();
                }
                break;
            case DialogResult.No: // Reset…
                var confirm = MessageBox.Show(
                    "This will replace the current token. Your other device "
                    + "will stop syncing until you paste the new token there. Proceed?",
                    "Reset token?", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (confirm != DialogResult.Yes) return;
                var fresh = ConfigStore.GenerateToken();
                if (TrySave(fresh))
                {
                    MessageBox.Show(
                        $"New token saved:\n{fresh}\n\nAnyClip will now quit. "
                        + "Relaunch to apply, then paste this token on your other device.",
                        "Token reset", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    quit();
                }
                break;
        }
    }

    private static bool TrySave(string token)
    {
        try { ConfigStore.Save(token); return true; }
        catch (Exception e)
        {
            MessageBox.Show(
                $"Saving to {ConfigStore.ConfigPath()} failed: {e.Message}",
                "Could not save token", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    /// Three-button chooser: [first]=Yes [second]=No [Close]=Cancel(default
    /// when closeIsDefault). WinForms has no 3-custom-button MessageBox, so
    /// a small fixed form keeps the flows native and thread-safe.
    private static Form BuildChoiceForm(
        string title, string body, string firstLabel, string secondLabel,
        bool closeIsDefault = false)
    {
        var form = new Form
        {
            Text = title, Width = 460, Height = 240,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false, MinimizeBox = false, TopMost = true,
        };
        var label = new Label { Left = 12, Top = 12, Width = 420, Height = 130, Text = body };
        var first = new Button { Text = firstLabel, DialogResult = DialogResult.Yes, Left = 12, Top = 155, Width = 200 };
        var second = new Button { Text = secondLabel, DialogResult = DialogResult.No, Left = 218, Top = 155, Width = 120 };
        var close = new Button { Text = closeIsDefault ? "Close" : "Cancel", DialogResult = DialogResult.Cancel, Left = 344, Top = 155, Width = 90 };
        form.Controls.AddRange(new Control[] { label, first, second, close });
        form.AcceptButton = closeIsDefault ? close : first;
        form.CancelButton = close;
        return form;
    }
}
