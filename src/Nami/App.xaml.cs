using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nami.Services;

namespace Nami;

public partial class App : Application
{
    private readonly AppServices _services;

    public static string LogPath { get; } = Path.Combine(AppSettings.Directory, "nami.log");

    public App()
    {
        SetupLogging();
        InitializeComponent();
        var settings = AppSettings.Load();
        L.Configure(settings.Language);
        _services = new AppServices(settings, History.Load());
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

    /// <summary>Diagnostic log line (file in %LOCALAPPDATA%\Nami). Stateless, so it stays static.</summary>
    public static void Log(string message)
    {
        System.Diagnostics.Trace.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var argv = Environment.GetCommandLineArgs().Skip(1).ToList();
        if (argv.Count > 0 && argv.All(a => a is "--register" or "--unregister"))
        {
            // Installer / uninstaller mode: no window.
            foreach (var a in argv)
            {
                try
                {
                    if (a == "--register") FileAssociation.Register(); else FileAssociation.Unregister();
                    Log($"{a}: ok");
                }
                catch (Exception ex) { Log($"{a} failed: {ex}"); }
            }
            Exit();
            return;
        }

        var window = _services.Windows.New();
        Log($"T+{Program.Uptime} ms window activated");
        window.Vm.FileLoaded += () => Log($"T+{Program.Uptime} ms file loaded: {window.Vm.FilePath}");
        window.Vm.PlaybackRestart += () => Log($"T+{Program.Uptime} ms first frame / playback started");
        HandleArguments(Environment.GetCommandLineArgs().Skip(1), window);
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

        var target = _services.Windows.Active ?? _services.Windows.All.LastOrDefault();
        var dispatcher = target?.DispatcherQueue;
        if (dispatcher is null) return;
        dispatcher.TryEnqueue(() =>
        {
            bool newWindow = _services.Settings.OpenInNewWindow || argv.Contains("--new-window");
            var window = newWindow || _services.Windows.Active is null ? _services.Windows.New() : _services.Windows.Active;
            HandleArguments(argv, window);
            window.BringToFront();
        });
    }

    /// <summary>Apply command-line arguments: files/URLs to play plus a few switches.</summary>
    private void HandleArguments(IEnumerable<string> args, MainWindow window)
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
            if (a.StartsWith("--preferences", StringComparison.Ordinal))
            {
                // --preferences[=section]: open the preferences window on start (used for UI checks).
                string? section = a.Length > 14 && a[13] == '=' ? a[14..] : null;
                window.DispatcherQueue.TryEnqueue(() => _ = window.ShowPreferencesAsync(section));
                continue;
            }
            if (a.StartsWith("--mpv-", StringComparison.Ordinal))
            {
                // --mpv-hwdec=no  →  mpv option hwdec=no (only effective for cores created afterwards)
                string body = a[6..];
                int eq = body.IndexOf('=');
                _services.Launch.Extra.Add(eq < 0 ? (body, "yes") : (body[..eq], body[(eq + 1)..]));
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
