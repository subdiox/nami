using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Input;
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
    private readonly InputNonClientPointerSource _nonClient;
    private Interop.CaptionButtonHook? _captionHook;
    private bool _fitOnNextVideoSize;
    private bool _compact;
    private RectInt32? _restoreBounds;

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

        // No system title bar at all (IINA hides its title bar with the HUD; the system caption
        // buttons cannot fade, so we draw our own). Resize borders stay. Dragging, double-click to
        // maximize and Aero Snap come from the caption region (SetTitleBar below).
        _presenter.SetBorderAndTitleBar(true, false);
        // Still required: without it the frame reserves a blank caption band whenever the window
        // has no caption (fullscreen presenter, mini player). The drag region comes from DragRegion.
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(DragRegion);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.ResizeClient(new SizeInt32(1280, 720));

        // The caption regions are non-client, so XAML never sees the pointer there: show the HUD
        // ourselves and mirror the hover state onto the maximize button.
        _nonClient = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _nonClient.PointerEntered += OnNonClientPointer;
        _nonClient.PointerMoved += OnNonClientPointer;
        _nonClient.PointerExited += (_, e) => { if (e.RegionKind == NonClientRegionKind.Maximize) SetMaximizeHover(false); };
        TitleOverlay.SizeChanged += (_, _) => UpdateNonClientRegions();
        TitleOverlay.Loaded += (_, _) => UpdateNonClientRegions();
        // Snap Layouts on hover (system maximize button) + our own click (full screen).
        _captionHook = new Interop.CaptionButtonHook(Hwnd, () => DispatcherQueue.TryEnqueue(() => { App.Log("caption: maximize button -> full screen"); Vm.ToggleFullscreen(); }));
        Closed += (_, _) => { _captionHook?.Dispose(); _captionHook = null; };

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
            if (_fitOnNextVideoSize && Vm.VideoSize.IsValid && !Vm.MusicMode && !_compact)
            {
                _fitOnNextVideoSize = false;
                FitToVideo((int)Vm.VideoSize.Width, (int)Vm.VideoSize.Height);
            }
        };
        Vm.Shutdown += Close;
        Activated += (_, e) =>
        {
            App.Log($"window: {e.WindowActivationState}");
            Main.WindowActive = e.WindowActivationState != WindowActivationState.Deactivated;
            if (!Main.WindowActive) Main.SetCursorHidden(false);
            else Main.FocusVideo();
        };
        AppWindow.Changed += OnAppWindowChanged;
        Closed += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            Vm.RecordPosition();
            Vm.History.Save();
        };

        Main.VideoView.PlayerCreated += _ => ApplyHdr();
        Main.Loaded += (_, _) => Services.L.Localize(TitleOverlay);
        // Fallback: if XAML focus is outside the page (or on nothing), key events still reach the
        // window root; route them to the player so shortcuts never silently die.
        RootGrid.AddHandler(UIElement.KeyDownEvent, new Microsoft.UI.Xaml.Input.KeyEventHandler((_, e) =>
        {
            var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Content.XamlRoot) as DependencyObject;
            if (focused is null || !IsInside(focused, Main)) { App.Log($"key (window fallback): {e.Key}"); Main.HandleKey(e); }
        }), true);
        _aspectLock = new Interop.AspectRatioLock(Hwnd);
        Closed += (_, _) => { _aspectLock?.Dispose(); _aspectLock = null; };
    }

    // ---- custom title bar -----------------------------------------------------------------

    /// <summary>
    /// Marks our maximize button as the system maximize button so Windows 11 shows the Snap Layouts
    /// flyout on hover; the click is intercepted by CaptionButtonHook. Cleared in full screen and
    /// mini mode, where the XAML Click handler takes over.
    /// </summary>
    private void UpdateNonClientRegions()
    {
        bool active = !IsFullScreen && !_compact && TitleOverlay.Visibility == Visibility.Visible
                      && CaptionButtons.Visibility == Visibility.Visible && MaximizeButton.ActualWidth > 0;
        if (active) _nonClient.SetRegionRects(NonClientRegionKind.Maximize, [ElementRect(MaximizeButton)]);
        else _nonClient.ClearRegionRects(NonClientRegionKind.Maximize);
    }

    private RectInt32 ElementRect(FrameworkElement e)
    {
        double scale = Content.XamlRoot?.RasterizationScale ?? Scale;
        var p = e.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new RectInt32((int)Math.Round(p.X * scale), (int)Math.Round(p.Y * scale),
            (int)Math.Round(e.ActualWidth * scale), (int)Math.Round(e.ActualHeight * scale));
    }

    private void OnNonClientPointer(InputNonClientPointerSource sender, NonClientPointerEventArgs e)
    {
        Main.ShowOverlay();
        SetMaximizeHover(e.RegionKind == NonClientRegionKind.Maximize);
    }

    private void SetMaximizeHover(bool on) =>
        MaximizeButton.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(on ? Color(0x33, 0xFF, 0xFF, 0xFF) : Colors.Transparent);

    /// <summary>The middle caption button is IINA's zoom button: it enters / leaves full screen.</summary>
    private void SyncMaximizeGlyph()
    {
        MaximizeIcon.Glyph = IsFullScreen ? "\uE923" : "\uE922";
        Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(MaximizeButton, L.T(IsFullScreen ? "Exit full screen (F11)" : "Full screen (F11)"));
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsFullScreen) Vm.SetFullscreen(false);   // the full-screen presenter cannot minimize
        _presenter.Minimize();
    }

    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleFullscreen();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private static Windows.UI.Color Color(byte a, byte r, byte g, byte b) => Windows.UI.Color.FromArgb(a, r, g, b);

    private static bool IsInside(DependencyObject? d, DependencyObject root)
    {
        for (; d is not null; d = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(d))
            if (d == root) return true;
        return false;
    }

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
                break;
            case nameof(PlayerViewModel.VideoSize):
            {
                var vs = Vm.VideoSize;
                if (_aspectLock is not null) _aspectLock.Aspect = vs.Aspect;
                if (vs.IsValid && _compact)
                {
                    // Mini player: keep the width, follow the new aspect.
                    var (cw, ch) = ClientPixelSize();
                    int h = (int)Math.Round(cw / vs.Aspect);
                    if (Math.Abs(h - ch) > 1) ResizeClientExact(cw, h);
                    break;
                }
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
    public bool IsMaximized => _presenter.State == OverlappedPresenterState.Maximized;

    private void ApplyFullscreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            if (_compact) ToggleCompactMode();
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        }
        else
        {
            AppWindow.SetPresenter(_presenter);
        }
        // Caption buttons stay in full screen too (they fade with the HUD); the middle one exits full screen.
        SyncMaximizeGlyph();
        UpdateNonClientRegions();
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

    private const int MiniWidthDip = 400;
    private const int MiniMarginDip = 24;

    /// <summary>IINA's PiP stand-in: a small, borderless, always-on-top window showing only the video.</summary>
    public void ToggleCompactMode()
    {
        if (IsFullScreen) Vm.SetFullscreen(false);
        if (Vm.MusicMode) SetMusicMode(false);
        _compact = !_compact;
        if (_compact)
        {
            _restoreBounds = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            // Keep the thin resize border: a fully borderless window (false, false) leaves a blank
            // caption strip at the top once the content no longer extends into the title bar.
            _presenter.SetBorderAndTitleBar(true, false);
            _presenter.IsAlwaysOnTop = true;
            _presenter.IsResizable = true;
            _presenter.IsMaximizable = false;
            _presenter.IsMinimizable = false;
            TitleOverlay.Visibility = Visibility.Collapsed;
            Main.SetMiniMode(true);

            double aspect = Vm.VideoSize.IsValid ? Vm.VideoSize.Aspect : 16.0 / 9;
            if (_aspectLock is not null) _aspectLock.Aspect = aspect;
            int w = (int)Math.Round(MiniWidthDip * Scale);
            int h = (int)Math.Round(w / aspect);
            int margin = (int)Math.Round(MiniMarginDip * Scale);
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            // Size the client area (the video) exactly, then park the window bottom-right.
            ResizeClientExact(w, h);
            var ws = AppWindow.Size;
            AppWindow.Move(new PointInt32(area.X + area.Width - ws.Width - margin, area.Y + area.Height - ws.Height - margin));
        }
        else
        {
            _presenter.SetBorderAndTitleBar(true, false);
            _presenter.IsAlwaysOnTop = Vm.OnTop;
            _presenter.IsMaximizable = true;
            _presenter.IsMinimizable = true;
            TitleOverlay.Visibility = Visibility.Visible;
            Main.SetMiniMode(false);
            if (_restoreBounds is { } r) AppWindow.MoveAndResize(r);
            if (_aspectLock is not null) _aspectLock.Aspect = Vm.VideoSize.IsValid ? Vm.VideoSize.Aspect : 0;
        }
        UpdateNonClientRegions();
    }

    public bool IsCompact => _compact;

    /// <summary>
    /// Start the system window-move loop from a client-area drag (what WPF's Window.DragMove does):
    /// SC_MOVE with HTCAPTION makes Windows treat the drag as a caption drag, including Aero Snap.
    /// </summary>
    public void BeginSystemMove()
    {
        Windows.Win32.PInvoke.ReleaseCapture();
        Windows.Win32.PInvoke.SendMessage((Windows.Win32.Foundation.HWND)Hwnd, Windows.Win32.PInvoke.WM_SYSCOMMAND,
            new Windows.Win32.Foundation.WPARAM(Windows.Win32.PInvoke.SC_MOVE | Windows.Win32.PInvoke.HTCAPTION), default);
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
        if (args.DidPresenterChange) SyncMaximizeGlyph();
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

    public Task ShowPreferencesAsync(string? section = null)
    {
        _services.Windows.ShowPreferences(Vm, section);
        return Task.CompletedTask;
    }

}
