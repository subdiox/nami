using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nami.Services;

namespace Nami;

public partial class App : Application
{
    /// <summary>All open player windows, in creation order.</summary>
    public static List<MainWindow> Windows { get; } = [];

    /// <summary>The most recently activated window; new files go here unless a new window is requested.</summary>
    public static MainWindow? ActiveWindow { get; set; }

    public static AppSettings Settings { get; private set; } = new();
    public static History History { get; private set; } = new();

    public static string LogPath { get; } = Path.Combine(AppSettings.Directory, "nami.log");

    public App()
    {
        SetupLogging();
        InitializeComponent();
        Settings = AppSettings.Load();
        History = History.Load();
        AppInstance.GetCurrent().Activated += OnRedirectedActivation;
        UnhandledException += (_, e) =>
        {
            Log("UNHANDLED XAML EXCEPTION: " + e.Message + Environment.NewLine + e.Exception);
        };
    }

    private static void SetupLogging()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppSettings.Directory);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000) File.Delete(LogPath);
            System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.TextWriterTraceListener(LogPath) { TraceOutputOptions = System.Diagnostics.TraceOptions.None });
            System.Diagnostics.Trace.AutoFlush = true;
            AppDomain.CurrentDomain.UnhandledException += (_, e) => Log($"UNHANDLED: {e.ExceptionObject}");
            TaskScheduler.UnobservedTaskException += (_, e) => Log($"UNOBSERVED TASK: {e.Exception}");
            Log($"---- Nami start {DateTime.Now:O} ---- T+{Program.Uptime} ms (Main reached)");
        }
        catch { }
    }

    public static void Log(string message)
    {
        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var window = NewWindow();
        Log($"T+{Program.Uptime} ms window activated");
        window.Vm.FileLoaded += () => Log($"T+{Program.Uptime} ms file loaded: {window.Vm.FilePath}");
        window.Vm.PlaybackRestart += () => Log($"T+{Program.Uptime} ms first frame / playback started");
        HandleArguments(Environment.GetCommandLineArgs().Skip(1), window);
    }

    /// <summary>Create and show a new player window.</summary>
    public static MainWindow NewWindow()
    {
        var window = new MainWindow();
        Windows.Add(window);
        ActiveWindow = window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) ActiveWindow = window;
        };
        window.Closed += (_, _) =>
        {
            Windows.Remove(window);
            if (ActiveWindow == window) ActiveWindow = Windows.LastOrDefault();
        };
        window.Activate();
        return window;
    }

    /// <summary>Another instance was started (e.g. from Explorer) and redirected to us.</summary>
    private void OnRedirectedActivation(object? sender, AppActivationArguments e)
    {
        string? cmdline = (e.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments;
        if (string.IsNullOrWhiteSpace(cmdline)) return;
        var argv = CommandLine.Split(cmdline);
        // The redirected command line includes the executable as argv[0].
        if (argv.Count > 0 && argv[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            argv.RemoveAt(0);

        var target = ActiveWindow ?? Windows.LastOrDefault();
        var dispatcher = target?.DispatcherQueue;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(() =>
        {
            bool newWindow = Settings.OpenInNewWindow || argv.Contains("--new-window");
            var window = newWindow || ActiveWindow is null ? NewWindow() : ActiveWindow;
            HandleArguments(argv, window);
            window.BringToFront();
        });
    }

    /// <summary>Apply command-line arguments: files/URLs to play plus a few switches.</summary>
    private static void HandleArguments(IEnumerable<string> args, MainWindow window)
    {
        bool first = true;
        foreach (var a in args)
        {
            switch (a)
            {
                case "--register":
                    try { FileAssociation.Register(); Log("file associations registered"); }
                    catch (Exception ex) { Log("register failed: " + ex); }
                    continue;
                case "--unregister":
                    try { FileAssociation.Unregister(); Log("file associations unregistered"); }
                    catch (Exception ex) { Log("unregister failed: " + ex); }
                    continue;
                case "--new-window":
                    continue;
            }
            if (a.StartsWith("--mpv-", StringComparison.Ordinal))
            {
                // --mpv-hwdec=no  →  mpv option hwdec=no (only effective before the core is created)
                string body = a[6..];
                int eq = body.IndexOf('=');
                Mpv.MpvPlayer.ExtraOptions.Add(eq < 0 ? (body, "yes") : (body[..eq], body[(eq + 1)..]));
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            string target = a;
            if (a.StartsWith(FileAssociation.UrlScheme + "://", StringComparison.OrdinalIgnoreCase))
            {
                target = FileAssociation.ParseSchemeUrl(a) ?? "";
                if (target.Length == 0) continue;
            }
            window.Vm.Open(target, append: !first);
            first = false;
        }
    }
}
