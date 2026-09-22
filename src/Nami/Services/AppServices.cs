namespace Nami.Services;

/// <summary>
/// Everything that used to be a process-wide singleton, created once by <see cref="App"/>
/// and handed down explicitly: windows → pages → view models → controls and dialogs.
/// </summary>
public sealed class AppServices
{
    public AppSettings Settings { get; }
    public History History { get; }
    public WindowManager Windows { get; }
    public MpvLaunchOptions Launch { get; } = new();

    public AppServices(AppSettings settings, History history)
    {
        Settings = settings;
        History = history;
        Windows = new WindowManager(this);
    }
}

/// <summary>Command-line "--mpv-name=value" options, applied to every core created afterwards.</summary>
public sealed class MpvLaunchOptions
{
    public List<(string name, string value)> Extra { get; } = [];
}

/// <summary>Tracks the open player windows and which one is active.</summary>
public sealed class WindowManager
{
    private readonly AppServices _services;
    private readonly List<MainWindow> _windows = [];

    public WindowManager(AppServices services) => _services = services;

    /// <summary>All open windows, in creation order.</summary>
    public IReadOnlyList<MainWindow> All => _windows;

    /// <summary>The most recently activated window; new files go here unless a new window is requested.</summary>
    public MainWindow? Active { get; private set; }

    /// <summary>Create, register and show a new player window.</summary>
    public MainWindow New()
    {
        var window = new MainWindow(_services);
        _windows.Add(window);
        Active = window;
        window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != Microsoft.UI.Xaml.WindowActivationState.Deactivated) Active = window;
        };
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (Active == window) Active = _windows.LastOrDefault();
        };
        window.Activate();
        return window;
    }

    private Preferences? _preferences;

    /// <summary>Show the (single) preferences window, optionally at a section.</summary>
    public void ShowPreferences(Player.PlayerViewModel vm, string? section = null)
    {
        if (_preferences is null)
        {
            _preferences = new Preferences(vm);
            _preferences.Closed += (_, _) => _preferences = null;
        }
        if (section is not null) _preferences.ShowSection(section);
        _preferences.Activate();
    }

    /// <summary>All live mpv cores (one per window that has finished loading).</summary>
    public IEnumerable<Mpv.MpvPlayer> Players => _windows.Select(w => w.Vm.Player).OfType<Mpv.MpvPlayer>();
}
