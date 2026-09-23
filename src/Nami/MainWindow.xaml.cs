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
    private Interop.NonClientHook? _ncHook;
    private Interop.BridgeEraseHook? _eraseHook;
    private bool _regionUpdateQueued;
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
        // The video area is the window caption (see UpdateNonClientRegions): Windows runs the drag,
        // with the Aero Snap preview. The hook restores our meaning of clicks on that area.
        _ncHook = new Interop.NonClientHook(Hwnd, new Interop.NonClientHook.Callbacks
        {
            OnMaximizeClick = () => DispatcherQueue.TryEnqueue(() => Vm.ToggleFullscreen()),
            IsVideoArea = IsVideoArea,
            OnVideoDoubleClick = () => DispatcherQueue.TryEnqueue(() => Vm.ToggleFullscreen()),
            OnVideoRightClick = (x, y) => DispatcherQueue.TryEnqueue(() => Main.ShowContextMenu(ScreenToPage(x, y))),
            OnVideoMiddleClick = () => DispatcherQueue.TryEnqueue(() => Vm.Keypress("MBTN_MID")),
            IsCursorHidden = () => Main.CursorHidden,
            ClampToMonitor = () => _clampToMonitor,
            OnFilesDropped = files => DispatcherQueue.TryEnqueue(() => Main.OpenDroppedFiles(files)),
            OnSizeMove = live =>
            {
                Main.VideoView.SetLiveResize(live);
                if (live)
                {
                    var shot = Vm.Player is { } p && !Vm.Idle ? p.ScreenshotRaw() : null;
                    if (shot is { } s) _eraseHook?.SetSnapshot(s.pixels, s.width, s.height, s.stride);
                }
                else _eraseHook?.SetSnapshot(null, 0, 0, 0);
            },
        });
        Closed += (_, _) => { _ncHook?.Dispose(); _ncHook = null; };
        // The island lags the frame by one frame while resizing; paint its erase with the video so the
        // strip continues the picture instead of flashing black.
        Main.Loaded += (_, _) => { _eraseHook = new Interop.BridgeEraseHook(Hwnd); };
        Closed += (_, _) => { _eraseHook?.Dispose(); _eraseHook = null; };
        _nonClient.PointerPressed += (_, e) => { if (e.RegionKind == NonClientRegionKind.Caption) _ncPress = e.Point; };
        _nonClient.PointerReleased += (_, e) =>
        {
            // A press + release without movement on the video is a click (the system drag never started).
            if (e.RegionKind != NonClientRegionKind.Caption || _ncPress is not { } p) return;
            _ncPress = null;
            if (Math.Abs(e.Point.X - p.X) <= 3 && Math.Abs(e.Point.Y - p.Y) <= 3 && IsVideoArea((int)e.Point.X, (int)e.Point.Y)) Main.OnVideoClick();
        };
        Main.SizeChanged += (_, _) => RequestRegionUpdate();

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
            if (focused is null || !IsInside(focused, Main)) Main.HandleKey(e);
        }), true);
        _aspectLock = new Interop.AspectRatioLock(Hwnd);
        Closed += (_, _) => { _aspectLock?.Dispose(); _aspectLock = null; };
    }

    // ---- custom title bar -----------------------------------------------------------------

    private Windows.Foundation.Point? _ncPress;

    /// <summary>Coalesced region refresh, run after the pending layout pass.</summary>
    public void RequestRegionUpdate()
    {
        if (_regionUpdateQueued) return;
        _regionUpdateQueued = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { _regionUpdateQueued = false; UpdateNonClientRegions(); });
    }

    /// <summary>
    /// Windowed mode: the title strip and the whole video area are the caption (Windows drags the
    /// window with the Aero Snap preview), minus the HUD controls while they are visible; the maximize
    /// button is the system maximize button (Snap Layouts on hover). Full screen and the mini player
    /// clear everything so XAML gets the pointer directly.
    /// </summary>
    private void UpdateNonClientRegions()
    {
        if (IsFullScreen || _compact || !Main.IsLoaded)
        {
            _nonClient.ClearAllRegionRects();
            return;
        }
        var caption = new List<RectInt32>();
        if (TitleOverlay.Visibility == Visibility.Visible && DragRegion.ActualWidth > 0) caption.Add(ElementRect(DragRegion));

        var holes = new List<RectInt32>();
        if (TitleOverlay.Visibility == Visibility.Visible && CaptionButtons.Visibility == Visibility.Visible) holes.Add(ElementRect(CaptionButtons));
        foreach (var el in Main.InteractiveOverlays()) holes.Add(ElementRect(el));
        var video = ElementRect(Main);
        var pieces = new List<RectInt32> { video };
        foreach (var hole in holes)
            pieces = pieces.SelectMany(r => Subtract(r, hole)).ToList();
        caption.AddRange(pieces.Where(r => r.Width > 0 && r.Height > 0));
        _nonClient.SetRegionRects(NonClientRegionKind.Caption, caption.ToArray());

        bool max = TitleOverlay.Visibility == Visibility.Visible && CaptionButtons.Visibility == Visibility.Visible && MaximizeButton.ActualWidth > 0;
        if (max) _nonClient.SetRegionRects(NonClientRegionKind.Maximize, [ElementRect(MaximizeButton)]);
        else _nonClient.ClearRegionRects(NonClientRegionKind.Maximize);
    }

    /// <summary>a minus b, as up to four rectangles.</summary>
    private static IEnumerable<RectInt32> Subtract(RectInt32 a, RectInt32 b)
    {
        int ax2 = a.X + a.Width, ay2 = a.Y + a.Height, bx2 = b.X + b.Width, by2 = b.Y + b.Height;
        if (b.X >= ax2 || bx2 <= a.X || b.Y >= ay2 || by2 <= a.Y) { yield return a; yield break; }
        int ix1 = Math.Max(a.X, b.X), iy1 = Math.Max(a.Y, b.Y), ix2 = Math.Min(ax2, bx2), iy2 = Math.Min(ay2, by2);
        if (iy1 > a.Y) yield return new RectInt32(a.X, a.Y, a.Width, iy1 - a.Y);                 // above
        if (iy2 < ay2) yield return new RectInt32(a.X, iy2, a.Width, ay2 - iy2);                 // below
        if (ix1 > a.X) yield return new RectInt32(a.X, iy1, ix1 - a.X, iy2 - iy1);               // left
        if (ix2 < ax2) yield return new RectInt32(ix2, iy1, ax2 - ix2, iy2 - iy1);               // right
    }

    /// <summary>Screen point inside the window but outside the title strip.</summary>
    private bool IsVideoArea(int x, int y)
    {
        if (TitleOverlay.Visibility != Visibility.Visible || DragRegion.ActualWidth <= 0) return true;
        var title = ElementRect(TitleOverlay);
        var pos = AppWindow.Position;
        int cx = x - pos.X, cy = y - pos.Y;   // ElementRect is relative to the window's client origin ≈ window origin (no caption)
        return !(cx >= title.X && cx < title.X + title.Width && cy >= title.Y && cy < title.Y + title.Height);
    }

    /// <summary>Screen pixels → page (XAML) coordinates.</summary>
    private unsafe Windows.Foundation.Point ScreenToPage(int x, int y)
    {
        var pt = new System.Drawing.Point(x, y);
        Windows.Win32.PInvoke.ScreenToClient((Windows.Win32.Foundation.HWND)Hwnd, ref pt);
        double scale = Content.XamlRoot?.RasterizationScale ?? Scale;
        return new Windows.Foundation.Point(pt.X / scale, pt.Y / scale);
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
    private bool _clampToMonitor;   // see NonClientHook.Callbacks.ClampToMonitor
    public bool IsMaximized => _presenter.State == OverlappedPresenterState.Maximized;

    private void ApplyFullscreen(bool on)
    {
        if (on == IsFullScreen) return;
        if (on)
        {
            if (_compact) ToggleCompactMode();
            _clampToMonitor = true;
            AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
            if (_eraseHook is not null) _eraseHook.FillParent = true;   // no 1 px line on the top edge
        }
        else
        {
            _clampToMonitor = false;
            if (_eraseHook is not null) _eraseHook.FillParent = false;
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

    private double Scale => Windows.Win32.PInvoke.GetDpiForWindow((Windows.Win32.Foundation.HWND)Hwnd) / 96.0;

    /// <summary>Client area = video size × scale (IINA's Cmd+0/1/2), centered, clamped to the work area.</summary>
    public void ResizeToVideoScale(double scale)
    {
        if (!Vm.VideoSize.IsValid || IsFullScreen || _compact || Vm.MusicMode) return;
        if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore();
        ResizeClientKeepingCenter((int)Math.Round(Vm.VideoSize.Width * scale), (int)Math.Round(Vm.VideoSize.Height * scale));
    }

    /// <summary>Window size × factor at the video aspect (IINA's Cmd+- / Cmd+=).</summary>
    public void ScaleWindow(double factor)
    {
        if (IsFullScreen || _compact || Vm.MusicMode) return;
        if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore();
        var (cw, ch) = ClientPixelSize();
        int w = (int)Math.Round(cw * factor);
        int h = Vm.VideoSize.IsValid ? (int)Math.Round(w / Vm.VideoSize.Aspect) : (int)Math.Round(ch * factor);
        ResizeClientKeepingCenter(w, h);
    }

    /// <summary>As large as the work area allows at the video aspect (IINA's Cmd+3).</summary>
    public void FitToScreen()
    {
        if (!Vm.VideoSize.IsValid || IsFullScreen || _compact || Vm.MusicMode) return;
        if (_presenter.State == OverlappedPresenterState.Maximized) _presenter.Restore();
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double s = Math.Min(area.Width / (double)Vm.VideoSize.Width, area.Height / (double)Vm.VideoSize.Height);
        ResizeClientKeepingCenter((int)Math.Round(Vm.VideoSize.Width * s), (int)Math.Round(Vm.VideoSize.Height * s));
    }

    private void ResizeClientKeepingCenter(int cw, int ch)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        double minW = 320 * Scale;
        if (cw < minW && Vm.VideoSize.IsValid) { cw = (int)minW; ch = (int)Math.Round(cw / Vm.VideoSize.Aspect); }
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        int cx = pos.X + size.Width / 2, cy = pos.Y + size.Height / 2;
        ResizeClientExact(cw, ch);
        var ns = AppWindow.Size;
        int nx = Math.Clamp(cx - ns.Width / 2, area.X, Math.Max(area.X, area.X + area.Width - ns.Width));
        int ny = Math.Clamp(cy - ns.Height / 2, area.Y, Math.Max(area.Y, area.Y + area.Height - ns.Height));
        AppWindow.Move(new PointInt32(nx, ny));
    }

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
        if (args.DidSizeChange || args.DidPresenterChange) RequestRegionUpdate();
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
        RequestRegionUpdate();
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
