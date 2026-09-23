using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Nami.Input;
using Nami.Interop;
using Nami.Player;
using Nami.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;

namespace Nami;

public sealed partial class MainPage : Page
{
    public PlayerViewModel Vm { get; }
    private static readonly Microsoft.UI.Input.InputCursor? HiddenCursor = EmptyCursor.Create();
    private static readonly Microsoft.UI.Input.InputCursor ArrowCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);
    private bool _cursorHidden;

    // Focus never stays on nothing while this window is active (keys would otherwise stop reaching the page).
    private void OnAnyGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        if (e.NewFocusedElement is null) FocusVideo();
    }

    private static string Describe(object? o)
    {
        if (o is not DependencyObject d) return "null";
        var parts = new List<string>();
        for (DependencyObject? cur = d; cur is not null && parts.Count < 8; cur = VisualTreeHelper.GetParent(cur))
        {
            string name = (cur as FrameworkElement)?.Name is { Length: > 0 } n ? $"#{n}" : "";
            string content = cur is ContentControl cc && cc.Content is not null ? $"[{cc.Content.GetType().Name}]" : "";
            parts.Add(cur.GetType().Name + name + content);
        }
        return string.Join(" < ", parts);
    }
    private static void OnAnyLostFocus(object? sender, FocusManagerLostFocusEventArgs e) { }

    /// <summary>Hide / show the mouse cursor over the player (IINA hides it together with the HUD).</summary>
    public void SetCursorHidden(bool hide)
    {
        if (_cursorHidden == hide || HiddenCursor is null) return;
        if (hide && !IsCursorOverPlayer()) return;
        _cursorHidden = hide;
        // The pointer is over Surface (the input layer above the video); set it there and on the page.
        Surface.SetCursor(hide ? HiddenCursor : null);
        ProtectedCursor = hide ? HiddenCursor : ArrowCursor;
        WindowInterop.RefreshCursor();
    }

    private bool IsCursorOverPlayer()
    {
        if (Window is not { } w) return false;
        var pos = WindowInterop.CursorPosition;
        var under = Windows.Win32.PInvoke.WindowFromPoint(pos);
        return under != default
            && Windows.Win32.PInvoke.GetAncestor(under, Windows.Win32.UI.WindowsAndMessaging.GET_ANCESTOR_FLAGS.GA_ROOT) == (Windows.Win32.Foundation.HWND)w.Hwnd;
    }
    public Controls.VideoView VideoView => Video;
    private MainWindow? Window => Vm.Window;

    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(2500);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clickTimer;
    private bool _overlayVisible = true;
    private bool _pointerOverControls;

    // drag-to-move state
    private Windows.Foundation.Point _pressPoint;
    private bool _leftDown;
    private bool _dragging;

    public MainPage(AppServices services)
    {
        Vm = new PlayerViewModel(services);
        InitializeComponent();
        Video.Vm = Vm;
        Osc.Vm = Vm;
        Osd.Configure(services.Settings.Osd);
        Vm.Osd = new OsdController(Vm, Osd.Show);
        Music.Vm = Vm;
        Sidebar.Bind(Vm);
        Mini.Vm = Vm;
        // Subscribed here, not in OnLoaded: files from the command line are opened before the page loads.
        Vm.YtDlpNeeded += (url, append) => _ = PromptYtDlpAsync(url, append);
        Mini.ExitRequested += () => Window?.ToggleCompactMode();
        Mini.CloseRequested += () => Window?.Close();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => TryHideOverlay();

        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = WindowInterop.DoubleClickTime;
        _clickTimer.IsRepeating = false;

        // Show the spinner only when opening or buffering takes a moment; local files never see it.
        _loadingTimer = DispatcherQueue.CreateTimer();
        _loadingTimer.Interval = TimeSpan.FromMilliseconds(400);
        _loadingTimer.IsRepeating = false;
        _loadingTimer.Tick += (_, _) => UpdateLoadingIndicator(show: true);
        _clickTimer.Tick += (_, _) =>
        {
            switch (Vm.Services.Settings.SingleClick)
            {
                case SingleClickAction.PauseResume: Vm.TogglePause(); break;
                case SingleClickAction.ToggleOsc: if (_overlayVisible) HideOverlayNow(); else ShowOverlay(); break;
            }
        };

        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            FocusManager.GotFocus -= OnAnyGotFocus;
            FocusManager.LostFocus -= OnAnyLostFocus;
            SetCursorHidden(false);
        };

        // Pointer input on the video surface
        Root.PointerMoved += OnPointerMoved;
        Root.PointerPressed += OnPointerPressed;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerExited += (_, _) => { if (!_dragging) _leftDown = false; };
        Root.PointerCaptureLost += (_, e) => EndDrag(e.Pointer);
        Root.PointerCanceled += (_, e) => EndDrag(e.Pointer);
        Root.PointerWheelChanged += OnPointerWheel;
        Root.RightTapped += OnRightTapped;
        Surface.Tapped += OnVideoTapped;
        Surface.DoubleTapped += OnVideoDoubleTapped;

        foreach (var c in new UIElement[] { Osc, Sidebar, Mini })
        {
            c.PointerEntered += (_, _) => { _pointerOverControls = true; ShowOverlay(); };
            c.PointerExited += (_, _) => { _pointerOverControls = false; RestartHideTimer(); };
        }

        Osc.PipRequested += () => Window?.ToggleCompactMode();
        Osc.MenuOpenChanged += SetMenuOpen;
        Osd.InteractiveChanged += () => Window?.RequestRegionUpdate();
        Osc.MusicModeRequested += () => Window?.SetMusicMode(true);
        ApplyOscLayout(Vm.Services.Settings.OscLayout);

        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    public void ShowMediaInfo() => _ = ShowDialogAsync(new InspectorDialog(Vm));
    public void ShowKeyBindings() => _ = ShowDialogAsync(new KeyBindingsDialog(Vm));

    private async Task ShowDialogAsync(ContentDialog dialog)
    {
        dialog.XamlRoot = XamlRoot;
        await dialog.ShowAsync();
        FocusVideo();
    }

    /// <summary>yt-dlp is missing for a streaming-site URL: offer the download, then open the URL.</summary>
    private async Task PromptYtDlpAsync(string url, bool append)
    {
        if (!IsLoaded)
        {
            // Startup: wait for the page (dialogs need a XamlRoot).
            var loaded = new TaskCompletionSource();
            RoutedEventHandler? once = null;
            once = (_, _) => { Loaded -= once; loaded.TrySetResult(); };
            Loaded += once;
            await loaded.Task;
        }
        var text = new TextBlock { Text = L.T("Playing this kind of URL requires yt-dlp. Download it and mpv will use it automatically."), TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        var dialog = new ContentDialog
        {
            Title = L.T("yt-dlp is required"),
            Content = new StackPanel { Children = { text, progress } },
            PrimaryButtonText = L.T("Download"),
            CloseButtonText = L.T("Cancel"),
            DefaultButton = ContentDialogButton.Primary,
            RequestedTheme = ElementTheme.Dark,
        };
        bool installed = false;
        dialog.PrimaryButtonClick += async (d, e) =>
        {
            var deferral = e.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            try
            {
                await YtDlp.InstallOrUpdateAsync(new Progress<double>(v => progress.Value = v * 100), CancellationToken.None);
                installed = true;
            }
            catch (Exception ex)
            {
                text.Text = L.T("Download failed: ") + ex.Message;
                progress.Visibility = Visibility.Collapsed;
                d.IsPrimaryButtonEnabled = true;
                e.Cancel = true;
            }
            finally { deferral.Complete(); }
        };
        await ShowDialogAsync(dialog);
        if (installed) Vm.Open(url, append);
    }

    /// <summary>False while another window (preferences, another player) is the active one.</summary>
    public bool WindowActive { get; set; } = true;

    /// <summary>Give keyboard focus back to the player (after dialogs, flyouts, window activation).</summary>
    public void FocusVideo()
    {
        if (!IsLoaded || XamlRoot is null) return;   // Activated fires before the page is loaded
        // Never while another window is active: Focus() would activate this window and steal the
        // keyboard from e.g. a text box in the preferences window.
        if (!WindowActive) return;
        // Deferred: let XAML finish its own focus restoration (window activation, popup close) first,
        // then take focus only if it ended up nowhere. Interfering mid-transition can leave the XAML
        // focus and the Win32 keyboard focus disagreeing, which kills every key.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || XamlRoot is null || !WindowActive) return;
            var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            if (focused is null || (!IsInside(focused, Sidebar) && focused is not TextBox and not NumberBox and not AutoSuggestBox))
                Focus(FocusState.Programmatic);
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        FocusManager.GotFocus += OnAnyGotFocus;
        FocusManager.LostFocus += OnAnyLostFocus;
        Focus(FocusState.Programmatic);
        Vm.PropertyChanged += OnVmChanged;
        Vm.Error += msg => Osd.Show(new OsdMessage(OsdMessage.IconInfo, L.T("Error"), msg, Seconds: 4));
        UpdateEmptyState();
        RestartHideTimer();
        // After the first frames are out: realize the sidebar so its first open is immediate.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, PrewarmSidebar);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Sidebar):
                SetSidebar(Vm.Sidebar);
                break;
            case nameof(PlayerViewModel.Idle):
            case nameof(PlayerViewModel.FilePath):
                UpdateEmptyState();
                break;
            case nameof(PlayerViewModel.Paused):
                if (Vm.Paused) ShowOverlay(); else RestartHideTimer();
                break;
            case nameof(PlayerViewModel.Loading):
            case nameof(PlayerViewModel.PausedForCache):
                if (Vm.Loading || Vm.PausedForCache) { if (!_loadingTimer.IsRunning && LoadingPanel.Visibility != Visibility.Visible) _loadingTimer.Start(); }
                else { _loadingTimer.Stop(); UpdateLoadingIndicator(show: false); }
                break;
        }
    }

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _loadingTimer;

    private void UpdateLoadingIndicator(bool show)
    {
        show = show && (Vm.Loading || Vm.PausedForCache);
        LoadingText.Text = L.T(Vm.Loading ? "Loading…" : "Buffering…");
        LoadingRing.IsActive = show;
        LoadingPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    public void ApplyOsdSettings(OsdSettings s) => Osd.Configure(s);

    public void ApplyOscLayout(Services.OscLayout layout)
    {
        Osc.Layout = layout;
        Osc.ApplyToolbar(Vm.Services.Settings.OscButtons);
        switch (layout)
        {
            case Services.OscLayout.Floating:
                Osc.HorizontalAlignment = HorizontalAlignment.Center;
                Osc.VerticalAlignment = VerticalAlignment.Bottom;
                Osc.Margin = new Thickness(0, 0, 0, 28);
                BottomShade.Visibility = Visibility.Visible;
                break;
            case Services.OscLayout.Bottom:
                Osc.HorizontalAlignment = HorizontalAlignment.Stretch;
                Osc.VerticalAlignment = VerticalAlignment.Bottom;
                Osc.Margin = new Thickness(0);
                BottomShade.Visibility = Visibility.Collapsed;
                break;
            case Services.OscLayout.Top:
                Osc.HorizontalAlignment = HorizontalAlignment.Stretch;
                Osc.VerticalAlignment = VerticalAlignment.Top;
                Osc.Margin = new Thickness(0, 44, 0, 0);
                BottomShade.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private bool _mini;

    /// <summary>Mini player: no OSC, title bar or sidebar; a small hover-only overlay instead.</summary>
    public void SetMiniMode(bool on)
    {
        _mini = on;
        Vm.CloseSidebar();
        Mini.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Osc.Visibility = on || Vm.MusicMode ? Visibility.Collapsed : Visibility.Visible;
        BottomShade.Visibility = on || Vm.MusicMode || Vm.Services.Settings.OscLayout != Services.OscLayout.Floating ? Visibility.Collapsed : Visibility.Visible;
        // Smaller OSD in the small window.
        var osd = Vm.Services.Settings.Osd;
        Osd.Configure(on ? new OsdSettings { Enabled = osd.Enabled, Position = osd.Position, Scale = OsdScale.Small, DurationSeconds = osd.DurationSeconds } : osd);
        ShowOverlay();
    }

    /// <summary>Music mode swaps the OSC for the music panel; the video area shows the cover art.</summary>
    public void SetMusicMode(bool on)
    {
        Music.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Osc.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        BottomShade.Visibility = on || Vm.Services.Settings.OscLayout != Services.OscLayout.Floating ? Visibility.Collapsed : Visibility.Visible;
        VideoHost.Margin = on ? new Thickness(0, 0, 0, Music.Height) : new Thickness(0);
        if (on) ShowOverlay();
        Window?.RequestRegionUpdate();
    }

    private void UpdateEmptyState()
        => EmptyState.Visibility = Vm.Idle && string.IsNullOrEmpty(Vm.FilePath) ? Visibility.Visible : Visibility.Collapsed;

    // ---- overlay auto-hide ------------------------------------------------------------

    public void ShowOverlay()
    {
        SetCursorHidden(false);
        if (!_overlayVisible)
        {
            _overlayVisible = true;
            Fade(Osc, 1);
            Fade(BottomShade, 1);
            Fade(Mini, 1);
            Window?.SetTitleOverlayVisible(true);
            Window?.RequestRegionUpdate();
        }
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void HideOverlayNow()
    {
        if (!_overlayVisible) return;
        _overlayVisible = false;
        Fade(Osc, 0);
        Fade(BottomShade, 0);
        Fade(Mini, 0);
        Window?.SetTitleOverlayVisible(false);
        Window?.RequestRegionUpdate();
    }

    private bool _menuOpen;

    /// <summary>While a menu / flyout is open the HUD must stay (menus belong to it).</summary>
    public void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        if (open) ShowOverlay(); else RestartHideTimer();
    }

    private void TryHideOverlay()
    {
        if (_menuOpen || _pointerOverControls || Vm.Paused || Vm.Idle || Vm.MusicMode) return;
        if (!_overlayVisible) return;
        _overlayVisible = false;
        Fade(Osc, 0);
        Fade(BottomShade, 0);
        Fade(Mini, 0);
        Window?.SetTitleOverlayVisible(false);
        Window?.RequestRegionUpdate();
        SetCursorHidden(true);
    }

    internal static void Fade(UIElement element, double to)
    {
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(180)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(anim, element);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        sb.Begin();
        element.IsHitTestVisible = to > 0;
    }

    // ---- sidebar ------------------------------------------------------------------------

    /// <summary>
    /// Realize the sidebar's panes so the first open (CC button, Ctrl+Shift+S) is immediate: the
    /// sidebar is shown invisibly (opacity 0) with every pane visible for a few frames, then parked
    /// off screen. It stays Visible from then on; closed means parked and not hit-testable.
    /// </summary>
    private void PrewarmSidebar()
    {
        if (Vm.Sidebar != SidebarKind.None || Sidebar.Visibility == Visibility.Visible) return;
        Sidebar.Opacity = 0;
        Sidebar.IsHitTestVisible = false;
        SidebarTransform.X = 0;
        Sidebar.Visibility = Visibility.Visible;
        Sidebar.PrewarmBegin();
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(150);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            Sidebar.PrewarmEnd();
            if (Vm.Sidebar == SidebarKind.None) SidebarTransform.X = Sidebar.Width;   // park
            Sidebar.Opacity = 1;
            FocusVideo();   // realizing the panes must not leave focus inside the (hidden) sidebar
        };
        timer.Start();
    }

    private bool SidebarClosed => Sidebar.Visibility == Visibility.Collapsed || !Sidebar.IsHitTestVisible;

    private void SetSidebar(SidebarKind kind)
    {
        Window?.RequestRegionUpdate();
        if (kind == SidebarKind.None)
        {
            var anim = new DoubleAnimation { To = Sidebar.Width, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(anim, SidebarTransform);
            Storyboard.SetTargetProperty(anim, "X");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) => { if (Vm.Sidebar == SidebarKind.None) Sidebar.IsHitTestVisible = false; Window?.RequestRegionUpdate(); };
            sb.Begin();
            Focus(FocusState.Programmatic);
            return;
        }

        Sidebar.Show(kind);
        bool wasClosed = SidebarClosed;
        Sidebar.Visibility = Visibility.Visible;
        Sidebar.IsHitTestVisible = true;
        if (wasClosed)
        {
            var anim = new DoubleAnimation { From = Sidebar.Width, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(anim, SidebarTransform);
            Storyboard.SetTargetProperty(anim, "X");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Begin();
        }
    }

    // ---- pointer ------------------------------------------------------------------------

    // Window drag: the pointer is captured and the window follows the cursor's screen-space
    // delta directly (like IINA), instead of handing off to the Win32 caption-drag loop.
    private Windows.Graphics.PointInt32 _dragWindowOrigin;
    private System.Drawing.Point _dragCursorOrigin;
    private const double DragThreshold = 3;

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        ShowOverlay();

        if (!_leftDown || Vm.Fullscreen || Window is not { } w) return;
        var p = e.GetCurrentPoint(Root).Position;

        if (!_dragging)
        {
            // Only the mini player gets here (in windowed mode the video is the caption and Windows drags).
            if (Math.Abs(p.X - _pressPoint.X) <= DragThreshold && Math.Abs(p.Y - _pressPoint.Y) <= DragThreshold) return;
            _clickTimer.Stop();
            _dragging = true;
            _dragWindowOrigin = w.AppWindow.Position;
            _dragCursorOrigin = WindowInterop.CursorPosition;
            Root.CapturePointer(e.Pointer);
        }

        var cur = WindowInterop.CursorPosition;
        w.AppWindow.Move(new Windows.Graphics.PointInt32(
            _dragWindowOrigin.X + (cur.X - _dragCursorOrigin.X),
            _dragWindowOrigin.Y + (cur.Y - _dragCursorOrigin.Y)));
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        if (e.OriginalSource is not UIElement src || !IsVideoSurface(src)) return;
        var pt = e.GetCurrentPoint(Root);
        if (pt.Properties.IsLeftButtonPressed)
        {
            _leftDown = true;
            _dragging = false;
            _pressPoint = pt.Position;
        }
        else if (pt.Properties.IsMiddleButtonPressed)
        {
            Vm.Keypress("MBTN_MID");
        }
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        EndDrag(e.Pointer);
        // WinUI's root ScrollViewer grabs focus on pointer release over non-focusable content,
        // which would silently disconnect every keyboard shortcut. Take it back afterwards.
        FocusVideo();
    }

    private void EndDrag(Microsoft.UI.Xaml.Input.Pointer? pointer)
    {
        _leftDown = false;
        if (!_dragging) return;
        _dragging = false;
        if (pointer is not null) Root.ReleasePointerCapture(pointer);
    }

    // ---- updates ------------------------------------------------------------------------

    private UpdateInfo? _pendingUpdate;

    /// <summary>
    /// Look up the newest release. Automatic checks run at most once a day, respect the setting
    /// and the skipped version, and stay silent on failure; manual checks always report back.
    /// </summary>
    public async Task<string?> CheckForUpdatesAsync(bool manual)
    {
        var s = Vm.Services.Settings;
        if (!manual)
        {
            if (!s.CheckForUpdates || (UpdateChecker.IsDevBuild && !UpdateChecker.TestMode)) return null;
            if ((DateTime.UtcNow - s.LastUpdateCheckUtc) < TimeSpan.FromHours(20)) return null;
        }
        UpdateInfo? info;
        try { info = await UpdateChecker.FetchLatestAsync(CancellationToken.None); }
        catch (Exception ex)
        {
            App.Log("update check: " + ex.Message);
            return manual ? L.F("Could not check for updates: {0}", ex.Message) : null;
        }
        s.LastUpdateCheckUtc = DateTime.UtcNow;
        s.Save();
        if (info is null || !UpdateChecker.IsNewer(info))
            return manual ? L.F("You have the latest version ({0}).", UpdateChecker.CurrentText) : null;
        if (!manual && s.SkippedUpdateVersion == info.Version.ToString(3)) return null;
        ShowUpdate(info);
        return L.F("Nami {0} is available.", info.Version.ToString(3));
    }

    private void ShowUpdate(UpdateInfo info)
    {
        _pendingUpdate = info;
        UpdateBar.Title = L.F("Nami {0} is available", info.Version.ToString(3));
        UpdateBar.Message = L.F("You are using {0}. Installed with winget? Run: winget upgrade {1}", UpdateChecker.CurrentText, UpdateChecker.WingetId);
        UpdateDownload.Content = L.T("Download");
        UpdateSkip.Content = L.T("Skip this version");
        UpdateBar.IsOpen = true;
        Window?.RequestRegionUpdate();
    }

    private async void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is { } u) await Launcher.LaunchUriAsync(new Uri(u.Url));
        UpdateBar.IsOpen = false;
    }

    private void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate is { } u)
        {
            Vm.Services.Settings.SkippedUpdateVersion = u.Version.ToString(3);
            Vm.Services.Settings.Save();
        }
        UpdateBar.IsOpen = false;
    }

    private void UpdateBar_Closed(InfoBar sender, InfoBarClosedEventArgs args) => Window?.RequestRegionUpdate();

    /// <summary>HUD elements that must stay clickable (excluded from the caption region while visible).</summary>
    public IEnumerable<FrameworkElement> InteractiveOverlays()
    {
        foreach (var el in new FrameworkElement[] { Osc, Sidebar, Music, Mini })
            if (el.Visibility == Visibility.Visible && el.IsHitTestVisible && el.ActualWidth > 0) yield return el;
        if (UpdateBar.IsOpen && UpdateBar.ActualWidth > 0) yield return UpdateBar;
        if (Osd.IsInteractive && Osd.ActualWidth > 0) yield return Osd;
    }

    public bool CursorHidden => _cursorHidden;

    /// <summary>A plain click on the video (from the non-client caption path): the configurable single-click action.</summary>
    public void OnVideoClick()
    {
        FocusVideo();
        if (Vm.Services.Settings.SingleClick == SingleClickAction.None) return;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_dragging || e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) return;
        if (Vm.Sidebar != SidebarKind.None && e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;
        // Single-click action (configurable; IINA does nothing by default), deferred past the double-click window.
        if (Vm.Services.Settings.SingleClick == SingleClickAction.None) return;
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _clickTimer.Stop();
        if (_mini) Window?.ToggleCompactMode();   // double-click leaves the mini player
        else Vm.ToggleFullscreen();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not UIElement src || !IsVideoSurface(src)) return;
        var props = e.GetCurrentPoint(Root).Properties;
        int delta = props.MouseWheelDelta;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (ctrl && !props.IsHorizontalMouseWheel)
        {
            // Touchpad pinch arrives as Ctrl+wheel; a mouse wheel with Ctrl zooms too (IINA gesture).
            Vm.SetZoom(Vm.VideoZoom + delta / 120.0 * 0.1);
            e.Handled = true;
            return;
        }
        if (props.IsHorizontalMouseWheel)
            Vm.Keypress(delta > 0 ? "WHEEL_RIGHT" : "WHEEL_LEFT");
        else
            Vm.Keypress(delta > 0 ? "WHEEL_UP" : "WHEEL_DOWN");
        e.Handled = true;
    }

    private bool IsVideoSurface(UIElement src)
    {
        // Anything that is not inside the OSC / sidebar counts as the video surface.
        DependencyObject? d = src;
        while (d is not null)
        {
            if (d == Osc || d == Sidebar) return false;
            if (d == Root) return true;
            d = VisualTreeHelper.GetParent(d);
        }
        return true;
    }

    private void OnRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not UIElement src || !IsVideoSurface(src)) return;
        ShowContextMenu(e.GetPosition(Root));
        e.Handled = true;
    }

    /// <summary>The player context menu at a page position.</summary>
    public void ShowContextMenu(Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        // Shortcuts shown on the right: app-level ones are fixed, mpv ones follow the current key bindings.
        menu.Items.Add(Item(Vm.Paused ? L.T("Play") : L.T("Pause"), Vm.TogglePause, Vm.KeyFor("cycle pause")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Open file…"), OpenFiles, "Ctrl+O"));
        menu.Items.Add(Item(L.T("Open URL…"), () => _ = OpenUrlAsync(), "Ctrl+U"));
        menu.Items.Add(Item(L.T("Open in new window…"), OpenFilesInNewWindow, "Ctrl+Alt+O"));
        menu.Items.Add(Item(L.T("New window"), () => Vm.Services.Windows.New(), "Ctrl+N"));
        var recent = new MenuFlyoutSubItem { Text = L.T("Recent files") };
        foreach (var h in Vm.History.Entries.Take(12))
            recent.Items.Add(Item(h.Display, () => Vm.Open(h.Path)));
        recent.IsEnabled = recent.Items.Count > 0;
        menu.Items.Add(recent);
        menu.Items.Add(Item(L.T("Add subtitle file…"), async () =>
        {
            var f = await Controls.Sidebar.PickFilesAsync(Window, [".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup"], multiple: false);
            if (f.Count > 0) Vm.AddSubtitle(f[0]);
        }));
        // IINA's Subtitles menu: a "Subtitle" and a "Secondary subtitle" selector, each with a <None> row.
        if (Vm.SubTracks.Count > 0)
        {
            menu.Items.Add(SubMenu(L.T("Subtitle"), Vm.PrimarySubChoices, secondary: false));
            menu.Items.Add(SubMenu(L.T("Secondary subtitle"), Vm.SecondarySubChoices, secondary: true));
            menu.Items.Add(Toggle(L.T("Show subtitles"), Vm.SubVisible, () => Vm.SetSubVisible(!Vm.SubVisible), Vm.KeyFor("cycle sub-visibility")));
            menu.Items.Add(Toggle(L.T("Show secondary subtitles"), Vm.SecondarySubVisible, () => Vm.SetSubVisible(!Vm.SecondarySubVisible, secondary: true), Vm.KeyFor("cycle secondary-sub-visibility")));
        }
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Video, audio & subtitles"), () => Vm.ToggleSidebar(SidebarKind.Settings), "Ctrl+Shift+S"));
        menu.Items.Add(Item(L.T("Playlist"), () => Vm.ToggleSidebar(SidebarKind.Playlist), "Ctrl+Shift+P"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Toggle(L.T("Full screen"), Vm.Fullscreen, Vm.ToggleFullscreen, "F11"));
        menu.Items.Add(Toggle(L.T("Always on top"), Vm.OnTop, Vm.ToggleOnTop, Vm.KeyFor("cycle ontop")));
        menu.Items.Add(Item(L.T("Mini player"), () => Window?.ToggleCompactMode(), "Ctrl+Shift+M"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L.T("Screenshot"), Vm.Screenshot, Vm.KeyFor("screenshot")));
        menu.Items.Add(Item(double.IsNaN(Vm.AbLoopA) ? L.T("A-B loop: set point A") : double.IsNaN(Vm.AbLoopB) ? L.T("A-B loop: set point B") : L.T("Clear A-B loop"), Vm.CycleAbLoop, Vm.KeyFor("ab-loop")));
        menu.Items.Add(Item(L.T("Frame step"), Vm.FrameStep, Vm.KeyFor("frame-step")));
        menu.Items.Add(Item(L.T("Frame back step"), Vm.FrameBackStep, Vm.KeyFor("frame-back-step")));
        menu.Items.Add(Item(L.T("Media info…"), () => _ = ShowDialogAsync(new InspectorDialog(Vm)), "Ctrl+I"));
        menu.Items.Add(Item(L.T("Key bindings…"), () => _ = ShowDialogAsync(new KeyBindingsDialog(Vm)), "Ctrl+Shift+K"));
        menu.Items.Add(Item(L.T("Preferences…"), () => Window?.ShowPreferencesAsync(), "Ctrl+,"));
        menu.Opened += (_, _) => SetMenuOpen(true);
        menu.Closed += (_, _) => { SetMenuOpen(false); FocusVideo(); };
        menu.ShowAt(Root, position);
    }

    public async Task OpenUrlAsync()
    {
        var dlg = new OpenUrlDialog { XamlRoot = XamlRoot };
        var result = await dlg.ShowAsync();
        FocusVideo();
        if (string.IsNullOrEmpty(dlg.Url)) return;
        if (result == ContentDialogResult.Primary) Vm.Open(dlg.Url);
        else if (result == ContentDialogResult.Secondary) Vm.Open(dlg.Url, append: true);
    }

    public async void OpenFiles()
    {
        var files = await Controls.Sidebar.PickFilesAsync(Window, FileAssociation.AllExtensions);
        if (files.Count > 0) Vm.OpenMany(files);
    }

    public async void OpenFilesInNewWindow()
    {
        var files = await Controls.Sidebar.PickFilesAsync(Window, FileAssociation.AllExtensions);
        if (files.Count > 0) Vm.Services.Windows.New().Vm.OpenMany(files);
    }

    // ---- keyboard ---------------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e) => HandleKey(e);

    private void OpenSettingsTab(int tab)
    {
        Vm.SettingsTab = tab;
        if (Vm.Sidebar != SidebarKind.Settings) Vm.ToggleSidebar(SidebarKind.Settings);
    }

    private void OpenPlaylistTab(int tab)
    {
        Vm.PlaylistTab = tab;
        if (Vm.Sidebar != SidebarKind.Playlist) Vm.ToggleSidebar(SidebarKind.Playlist);
    }

    private void RevealCurrentFile()
    {
        string? path = Vm.FilePath;
        if (string.IsNullOrEmpty(path) || path.Contains("://") || !File.Exists(path)) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch (Exception ex) { App.Log("reveal: " + ex.Message); }
    }

    /// <summary>Player shortcuts; also called by the window when focus is outside this page.</summary>
    public void HandleKey(KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        // Let text boxes and the sidebar handle their own keys.
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is TextBox or NumberBox or AutoSuggestBox || (Vm.Sidebar != SidebarKind.None && IsInside(focused, Sidebar))) return;

        var mods = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        bool ctrl = (mods & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        // App-level shortcuts first.
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.O when ctrl && alt: OpenFilesInNewWindow(); break;
            case VirtualKey.O when ctrl: OpenFiles(); break;
            case VirtualKey.N when ctrl: Vm.Services.Windows.New(); break;
            case VirtualKey.U when ctrl: _ = OpenUrlAsync(); break;
            case VirtualKey.I when ctrl: _ = ShowDialogAsync(new InspectorDialog(Vm)); break;
            case VirtualKey.K when ctrl && shift: _ = ShowDialogAsync(new KeyBindingsDialog(Vm)); break;
            case VirtualKey.P when ctrl && shift: Vm.ToggleSidebar(SidebarKind.Playlist); break;
            case VirtualKey.S when ctrl && shift: OpenSettingsTab(2); break;
            case VirtualKey.M when ctrl && shift: Window?.ToggleCompactMode(); break;
            case (VirtualKey)0xBC when ctrl: _ = Window?.ShowPreferencesAsync(); break;   // Ctrl+,
            case VirtualKey.F11: Vm.ToggleFullscreen(); break;
            // IINA: Cmd+0/1/2 window at half / actual / double size, Cmd+3 fit to screen, Cmd+-/= smaller / bigger
            case VirtualKey.Number0 when ctrl && !shift: Window?.ResizeToVideoScale(0.5); break;
            case VirtualKey.Number1 when ctrl && !shift: Window?.ResizeToVideoScale(1); break;
            case VirtualKey.Number2 when ctrl && !shift: Window?.ResizeToVideoScale(2); break;
            case VirtualKey.Number3 when ctrl && !shift: Window?.FitToScreen(); break;
            case (VirtualKey)0xBD when ctrl: Window?.ScaleWindow(1 / 1.1); break;   // Ctrl+-
            case (VirtualKey)0xBB when ctrl: Window?.ScaleWindow(1.1); break;       // Ctrl+=
            // IINA: Shift+Cmd+v / a / s panels, Shift+Cmd+c chapters
            case VirtualKey.V when ctrl && shift: OpenSettingsTab(0); break;
            case VirtualKey.A when ctrl && shift: OpenSettingsTab(1); break;
            case VirtualKey.C when ctrl && shift: OpenPlaylistTab(1); break;
            // IINA: Ctrl+Cmd+p picture in picture, Alt+Cmd+m music mode
            case VirtualKey.P when ctrl && alt: Window?.ToggleCompactMode(); break;
            case VirtualKey.M when ctrl && alt: Window?.SetMusicMode(!Vm.MusicMode); break;
            // IINA: Shift+Cmd+r reveal in Finder, Cmd+d find online subtitles
            case VirtualKey.R when ctrl && shift: RevealCurrentFile(); break;
            case VirtualKey.D when ctrl && !shift: OpenSettingsTab(2); break;
            case VirtualKey.Escape when Vm.Sidebar != SidebarKind.None: Vm.CloseSidebar(); break;
            default: handled = false; break;
        }
        if (handled) { e.Handled = true; return; }

        string? key = MpvKeyMapper.Map(e.Key, ctrl, alt, shift);
        if (key is null) return;
        Vm.Keypress(key);
        ShowOverlay();
        e.Handled = true;
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        while (element is not null)
        {
            if (element == container) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    // ---- drag & drop --------------------------------------------------------------------

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems) || e.DataView.Contains(StandardDataFormats.Text)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
        e.DragUIOverride.Caption = L.T("Play with Nami");
    }

    /// <summary>Files dropped on the player (XAML drop or shell drop on the caption area).</summary>
    public void OpenDroppedFiles(List<string> paths)
    {
        if (paths.Count == 1 && IsSubtitle(paths[0]) && !Vm.Idle)
            Vm.AddSubtitle(paths[0]);
        else if (paths.Count > 0)
            Vm.OpenMany(paths, append: Vm.Sidebar == SidebarKind.Playlist);
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            OpenDroppedFiles(items.OfType<StorageFile>().Select(f => f.Path).ToList());
        }
        else if (e.DataView.Contains(StandardDataFormats.Text))
        {
            string text = (await e.DataView.GetTextAsync()).Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out _)) Vm.Open(text);
        }
    }

    private static bool IsSubtitle(string path)
        => Path.GetExtension(path).ToLowerInvariant() is ".srt" or ".ass" or ".ssa" or ".sub" or ".vtt" or ".sup";

    // Click handlers instead of ICommand: a managed ICommand cannot be marshaled to WinRT under NativeAOT
    // (CCW creation fails), which crashed the context menu in published builds.
    private MenuFlyoutSubItem SubMenu(string text, IEnumerable<TrackInfo> choices, bool secondary)
    {
        var sub = new MenuFlyoutSubItem { Text = text };
        foreach (var t in choices)
        {
            var choice = t;
            sub.Items.Add(Toggle(t.Display, t.Selected, () => Vm.ChooseSub(choice, secondary)));
        }
        return sub;
    }

    private static MenuFlyoutItem Item(string text, Action action, string? accelerator = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
        item.Click += (_, _) => action();
        return item;
    }

    private static ToggleMenuFlyoutItem Toggle(string text, bool isChecked, Action action, string? accelerator = null)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
        if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
        item.Click += (_, _) => action();
        return item;
    }
}
