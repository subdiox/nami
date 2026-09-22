using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nami.Player;
using Nami.Services;

namespace Nami;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public static PlayerViewModel Vm { get; } = new();
    public static AppSettings Settings { get; private set; } = new();

    public static string LogPath { get; } = Path.Combine(AppSettings.Directory, "nami.log");

    public App()
    {
        SetupLogging();
        InitializeComponent();
        Settings = AppSettings.Load();
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
        Window = new MainWindow();
        Window.Activate();
        Log($"T+{Program.Uptime} ms window activated");
        Vm.FileLoaded += () => Log($"T+{Program.Uptime} ms file loaded: {Vm.FilePath}");
        Vm.PlaybackRestart += () => Log($"T+{Program.Uptime} ms first frame / playback started");
        OpenFromCommandLine(Environment.GetCommandLineArgs().Skip(1));
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

        Window?.DispatcherQueue.TryEnqueue(() =>
        {
            OpenFromCommandLine(argv);
            Window?.BringToFront();
        });
    }

    private static void OpenFromCommandLine(IEnumerable<string> args)
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
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            Vm.Open(a, append: !first);
            first = false;
        }
    }
}
