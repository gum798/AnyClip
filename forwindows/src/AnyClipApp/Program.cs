using AnyClip.Core;

namespace AnyClip.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        string stateDir = ConfigStore.DefaultDir();
        string logFile = Path.Combine(stateDir, "anyclip.log");
        RotatingLog.Shared = new RotatingLog(logFile);

        string? token = Dialogs.ResolveToken();
        if (token is null)
        {
            Console.Error.WriteLine("anyclip: onboarding cancelled, exiting");
            return;
        }

        string appVersion =
            System.Reflection.Assembly.GetExecutingAssembly()
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion.Split('+')[0]
            ?? "0.0.0-dev";

        using var quitCts = new CancellationTokenSource();
        Daemon? daemon = null;
        TrayIcon? tray = null;
        Task? daemonTask = null;

        void Quit()
        {
            quitCts.Cancel();
            // Give cleanup (mDNS deregister, pid release) up to 3 s, matching
            // the Python supervisor.stop(timeout=3) / macOS port behavior.
            daemonTask?.Wait(TimeSpan.FromSeconds(3));
            tray?.Dispose();
            Application.Exit();
        }

        tray = new TrayIcon(logFile, Quit);
        var notifier = new Notifier(tray.Notify);

        // STA invoker: a hidden UI-thread control all clipboard access AND
        // balloon tips are marshalled through (daemon tasks call
        // ApplyRemote/Notify off-thread).
        var staInvoker = new Control();
        _ = staInvoker.Handle; // force handle creation on this (UI) thread
        var winClipboard = new WinFormsClipboard();
        var clipboard = new ClipboardWatcher(
            winClipboard, Path.Combine(stateDir, "received"));
        winClipboard.Invoker = staInvoker;
        notifier.Invoker = staInvoker;

        // Captured BEFORE the daemon exists: onFatal fires on daemon
        // threadpool threads and MessageBox/Application.Exit must run on
        // the UI thread.
        var uiContext = SynchronizationContext.Current!;

        var mdns = new MdnsBeacon(() => daemon?.CurrentDirectory);
        daemon = new Daemon(
            new DaemonConfig(token, Wire.DefaultPort, Environment.MachineName),
            appVersion, stateDir,
            clipboard, mdns, new WindowsPidLock(),
            MdnsBeacon.PrimaryIPv4,
            notify: (title, body) => notifier.Notify(title, body),
            onFatal: message => uiContext.Post(_ =>
            {
                MessageBox.Show(message, "AnyClip cannot start",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                Application.Exit();
            }, null));

        // Clipboard events come from the UI loop; forward to the watcher.
        using var listener = new ClipboardListenerWindow(
            clipboard.HandleClipboardUpdateAsync);

        daemonTask = Task.Run(() => daemon.RunForeverAsync(quitCts.Token));

        // Fold daemon events into tray state on the UI thread.
        _ = Task.Run(async () =>
        {
            var state = PeerUiState.Initial;
            await foreach (var ev in daemon.Events.ReadAllAsync())
            {
                state = PeerStateReducer.Reduce(state, ev,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0);
                var snapshot = state;
                uiContext.Post(_ => tray.Apply(snapshot), null);
            }
        });

        Application.Run();
    }
}
