using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Nami.Player;
using Nami.Services;
using Windows.Graphics;

namespace Nami;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainPage Main;
    public PlayerViewModel Vm => Main.Vm;
    private Interop.AspectRatioLock? _aspectLock;
    public Interop.DisplayInfo? LastDisplay { get; private set; }
    public bool IsHdrPassthrough { get; private set; }
    private readonly OverlappedPresenter _presenter;
    private bool _fitOnNextVideoSize;
    private bool _compact;
    private RectInt32? _restoreBounds;
    private Preferences? _preferencesDialog;

    public nint Hwnd { get; }
    public MainPage Page => Main;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Main = new MainPage(services);
        RootGrid.Children.Insert(0, Main);
        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Vm.Window = this;

        _presenter = OverlappedPresenter.Create();
        AppWindow.SetPresenter(_presenter);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.ResizeClient(new SizeInt32(1280, 720));

        if (AppWindow.TitleBar is { } tb)
        {
            tb.ButtonBackgroundColor = Colors.Transparent;
            tb.ButtonInactiveBackgroundColor = Colors.Transparent;
            tb.ButtonForegroundColor = Colors.White;
            tb.ButtonInactiveForegroundColor = Color(0x99, 0xFF, 0xFF, 0xFF);
            tb.ButtonHoverBackgroundColor = Color(0x33, 0xFF, 0xFF, 0xFF);
            tb.ButtonHoverForegroundColor = Colors.White;
            tb.ButtonPressedBackgroundColor = Color(0x55, 0xFF, 0xFF, 0xFF);
            tb.ButtonPressedForegroundColor = Colors.White;
        }

        Vm.PropertyChanged += OnVmChanged;
        Vm.FileLoaded += () => _fitOnNextVideoSize = _services.Settings.ResizeWindowToVideo;
        Vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerViewModel.IsAudioOnly) || !_services.Settings.AutoMusicMode) return;
            // Audio file → music mode; video file → back to the normal window.
            if (Vm.IsAudioOnly && !Vm.MusicMode) SetMusicMode(true);
            else if (!Vm.IsAudioOnly && Vm.MusicMode && Vm.VideoTracks.Count > 0) SetMusicMode(false);
        };
        Vm.PlaybackRestart += () =>
        {
            // Video parameters are final once the first frame is out; fit the window now.
            if (_fitOnNextVideoSize && Vm.VideoSize.IsValid && !Vm.MusicMode)
            {
                _fitOnNextVideoSize = false;
                FitToVideo((int)Vm.VideoSize.Width, (int)Vm.VideoSize.Height);
            }
        };
        Vm.Shutdown += Close;
        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            Vm.RecordPosition();
            Vm.History.Save();
        };

        Main.VideoView.PlayerCreated += _ => ApplyHdr();
        Main.Loaded += (_, _) => Services.L.Localize(TitleOverlay);
        _aspectLock = new Interop.AspectRatioLock(Hwnd);
        Closed += (_, _) => { _aspectLock?.Dispose(); _aspectLock = null; };
    }

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    // ---- view model -> window ----------------------------------------------------------

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.MediaTitle):
                string t = string.IsNullOrEmpty(Vm.MediaTitle) ? "Nami" : Vm.MediaTitle;
                TitleText.Text = t;
                Title = t == "Nami" ? "Nami" : $"{t} - Nami";
                break;
            case nameof(PlayerViewModel.Fullscreen):
                ApplyFullscreen(Vm.Fullscreen);
                break;
            case nameof(PlayerViewModel.OnTop):
                _presenter.IsAlwaysOnTop = Vm.OnTop || _compact;
                PinButton.Visibility = Vm.OnTop ? Visibility.Visible : Visibility.Collapsed;
                break;
            case nameof(PlayerViewModel.VideoSize):
            {
                var vs = Vm.VideoSize;
                if (_aspectLock is not null) _aspectLock.Aspect = vs.Aspect;
                if (vs.IsValid && !_fitOnNextVideoSize && !IsFullScreen && !_compact && !Vm.MusicMode
                    && _presenter.State != OverlappedPresenterState.Maximized)
                {
                    // Aspect changed after the initial fit (rotation / override): keep the width, fix the height.
                    var (cw, chNow) = ClientPixelSize();
                    int h = (int)Math.Round(cw / vs.Aspect);
                    if (Math.Abs(h - chNow) > 1) ResizeClientExact(cw, h);
                }
                break;
            }
        }
    }

    // ---- fullscreen / compact -----------------------------------------------------------

    public bool IsFullScreen => AppWindow.Presenter.Kind == AppWindowPresenterKind.FullScreen;

    private void ApplyFullscreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            if (_compact) ToggleCompactMode();
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            CaptionSpacer.Width = new GridLength(8);
        }
        else
        {
            AppWindow.SetPresenter(_presenter);
            CaptionSpacer.Width = new GridLength(140);
        }
    }

    private RectInt32? _musicRestoreBounds;
    private const int MusicWidthDip = 320;
    private const int MusicPanelHeightDip = 150;

    /// <summary>IINA music mode: a narrow window with cover art on top and transport controls below.</summary>
    public void SetMusicMode(bool on)
    {
        if (Vm.MusicMode == on) return;
        if (on && IsFullScreen) Vm.SetFullscreen(false);
        if (on && _compact) ToggleCompactMode();
        Vm.MusicMode = on;
        Main.SetMusicMode(on);
        if (on)
        {
            _musicRestoreBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            if (_aspectLock is not null) _aspectLock.Aspect = 0;
            int w = (int)(MusicWidthDip * Scale);
            int h = (int)((MusicWidthDip + MusicPanelHeightDip) * Scale);
            _presenter.IsResizable = false;
            _presenter.IsMaximizable = false;
            ResizeClientExact(w, h);
        }
        else
        {
            _presenter.IsResizable = true;
            _presenter.IsMaximizable = true;
            if (_musicRestoreBounds is { } r) AppWindow.MoveAndResize(r);
            if (Vm.VideoSize.IsValid)
            {
                if (_aspectLock is not null) _aspectLock.Aspect = Vm.VideoSize.Aspect;
                _fitOnNextVideoSize = false;
                FitToVideo((int)Vm.VideoSize.Width, (int)Vm.VideoSize.Height);
            }
        }
    }

    /// <summary>IINA's PiP stand-in: a small, borderless, always-on-top window.</summary>
    public void ToggleCompactMode()
    {
        if (IsFullScreen) Vm.SetFullscreen(false);
        _compact = !_compact;
        if (_compact)
        {
            _restoreBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            _presenter.SetBorderAndTitleBar(false, false);
            _presenter.IsAlwaysOnTop = true;
            _presenter.IsResizable = true;
            TitleOverlay.Visibility = Visibility.Collapsed;

            double aspect = Vm.VideoWidth > 0 && Vm.VideoHeight > 0 ? (double)Vm.VideoWidth / Vm.VideoHeight : 16.0 / 9;
            int w = (int)(480 * Scale);
            int h = (int)(w / aspect);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            AppWindow.MoveAndResize(new RectInt32(area.X + area.Width - w - 24, area.Y + area.Height - h - 24, w, h));
        }
        else
        {
            _presenter.SetBorderAndTitleBar(true, true);
            _presenter.IsAlwaysOnTop = Vm.OnTop;
            TitleOverlay.Visibility = Visibility.Visible;
            if (_restoreBounds is { } r) AppWindow.MoveAndResize(r);
        }
    }

    private double Scale => Windows.Win32.PInvoke.GetDpiForWindow((Windows.Win32.Foundation.HWND)Hwnd) / 96.0;

    /// <summary>Resize the window so the client area matches the video aspect (IINA does this on open).</summary>
    private void FitToVideo(int videoW, int videoH)
    {
        if (IsFullScreen || _compact || _presenter.State == OverlappedPresenterState.Maximized) return;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        // IINA: open at the video's native size, shrunk only if it doesn't fit the screen.
        double maxW = area.Width * 0.9;
        double maxH = area.Height * 0.9;
        double minW = 480 * Scale;
        double w = videoW;
        double h = videoH;
        double s = Math.Min(1.0, Math.Min(maxW / w, maxH / h));
        if (w * s < minW) s = Math.Min(minW / w, Math.Min(maxW / w, maxH / h));
        int cw = (int)Math.Round(w * s);
        int ch = (int)Math.Round(h * s);

        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        int cx = pos.X + size.Width / 2;
        int cy = pos.Y + size.Height / 2;
        ResizeClientExact(cw, ch);
        var ns = AppWindow.Size;
        App.Log($"FitToVideo video={videoW}x{videoH} work={area.Width}x{area.Height} s={s:F3} client={cw}x{ch} -> window {ns.Width}x{ns.Height} client {ClientPixelSize().w}x{ClientPixelSize().h}");
        int nx = Math.Clamp(cx - ns.Width / 2, area.X, Math.Max(area.X, area.X + area.Width - ns.Width));
        int ny = Math.Clamp(cy - ns.Height / 2, area.Y, Math.Max(area.Y, area.Y + area.Height - ns.Height));
        AppWindow.Move(new PointInt32(nx, ny));
    }

    /// <summary>Real client-area size in pixels (AppWindow.ClientSize excludes the custom title bar).</summary>
    private unsafe (int w, int h) ClientPixelSize()
    {
        Windows.Win32.Foundation.RECT rc;
        Windows.Win32.PInvoke.GetClientRect((Windows.Win32.Foundation.HWND)Hwnd, &rc);
        return (rc.right - rc.left, rc.bottom - rc.top);
    }

    /// <summary>ResizeClient, then correct for the title-bar offset AppWindow adds under ExtendsContentIntoTitleBar.</summary>
    private void ResizeClientExact(int cw, int ch)
    {
        AppWindow.ResizeClient(new SizeInt32(cw, ch));
        var (aw, ah) = ClientPixelSize();
        if (aw != cw || ah != ch)
            AppWindow.ResizeClient(new SizeInt32(cw - (aw - cw), ch - (ah - ch)));
    }

    private string? _lastDisplayDevice;

    private void OnAppWindowChanged(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!args.DidPositionChange) return;
        // Re-evaluate HDR when the window lands on another monitor.
        var info = Interop.DisplayInfo.Query(Hwnd);
        if (info?.DeviceName != _lastDisplayDevice)
        {
            _lastDisplayDevice = info?.DeviceName;
            ApplyHdr();
        }
    }

    public void ApplyHdr()
    {
        if (Vm.Player is { } p)
        {
            (LastDisplay, IsHdrPassthrough) = HdrController.Apply(p, Hwnd, _services.Settings.HdrMode);
            _lastDisplayDevice = LastDisplay?.DeviceName;
        }
    }

    // ---- overlay ----------------------------------------------------------------------

    public void SetTitleOverlayVisible(bool visible)
    {
        if (_compact) return;
        MainPage.Fade(TitleOverlay, visible ? 1 : 0);
        TitleOverlay.IsHitTestVisible = true; // keep the drag region usable even when faded
    }

    public void BringToFront()
    {
        if (_presenter.State == OverlappedPresenterState.Minimized) _presenter.Restore();
        Activate();
    }

    public async Task ShowPreferencesAsync()
    {
        if (_preferencesDialog is not null) return;
        _preferencesDialog = new Preferences(Vm) { XamlRoot = Content.XamlRoot };
        try { await _preferencesDialog.ShowAsync(); }
        finally { _preferencesDialog = null; }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleOnTop();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Settings);
    private void PlaylistButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Playlist);
}
