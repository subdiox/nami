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

    // Focus diagnostics (cheap; helps pin down "keys stopped working" reports).
    private void OnAnyGotFocus(object? sender, FocusManagerGotFocusEventArgs e)
    {
        App.Log($"focus -> {Describe(e.NewFocusedElement)}");
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
    private static void OnAnyLostFocus(object? sender, FocusManagerLostFocusEventArgs e)
        => App.Log($"focus lost from {e.OldFocusedElement?.GetType().Name ?? "null"}");

    /// <summary>Hide / show the mouse cursor over the player (IINA hides it together with the HUD).</summary>
    public void SetCursorHidden(bool hide)
    {
        if (_cursorHidden == hide || HiddenCursor is null) return;
        if (hide && !IsCursorOverPlayer()) { App.Log("cursor: hide skipped (not over player)"); return; }
        _cursorHidden = hide;
        // The pointer is over Surface (the input layer above the video); set it there and on the page.
        Surface.SetCursor(hide ? HiddenCursor : null);
        ProtectedCursor = hide ? HiddenCursor : ArrowCursor;
        App.Log($"cursor: {(hide ? "hidden" : "shown")} ({WindowInterop.CursorState()})");
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
        Mini.ExitRequested += () => Window?.ToggleCompactMode();
        Mini.CloseRequested += () => Window?.Close();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => TryHideOverlay();

        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = WindowInterop.DoubleClickTime;
        _clickTimer.IsRepeating = false;
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
        }
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
        Video.Margin = on ? new Thickness(0, 0, 0, Music.Height) : new Thickness(0);
        if (on) ShowOverlay();
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

    private void SetSidebar(SidebarKind kind)
    {
        if (kind == SidebarKind.None)
        {
            var anim = new DoubleAnimation { To = Sidebar.Width, Duration = new Duration(TimeSpan.FromMilliseconds(200)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
            Storyboard.SetTarget(anim, SidebarTransform);
            Storyboard.SetTargetProperty(anim, "X");
            var sb = new Storyboard();
            sb.Children.Add(anim);
            sb.Completed += (_, _) => { if (Vm.Sidebar == SidebarKind.None) Sidebar.Visibility = Visibility.Collapsed; };
            sb.Begin();
            Focus(FocusState.Programmatic);
            return;
        }

        Sidebar.Show(kind);
        bool wasCollapsed = Sidebar.Visibility == Visibility.Collapsed;
        Sidebar.Visibility = Visibility.Visible;
        if (wasCollapsed)
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
            if (Math.Abs(p.X - _pressPoint.X) <= DragThreshold && Math.Abs(p.Y - _pressPoint.Y) <= DragThreshold) return;
            _clickTimer.Stop();
            if (!w.IsCompact)
            {
                // Normal window: hand the drag to the system's move loop, exactly like dragging the
                // title bar (Aero Snap at the screen edges, restore-from-maximized, Snap groups).
                _leftDown = false;
                w.BeginSystemMove();
                return;
            }
            // Mini player: plain move, no snapping (a picture-in-picture window should not be snapped to half a screen).
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
        menu.ShowAt(Root, e.GetPosition(Root));
        e.Handled = true;
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

    /// <summary>Player shortcuts; also called by the window when focus is outside this page.</summary>
    public void HandleKey(KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        // Let text boxes and the sidebar handle their own keys.
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        App.Log($"key: {e.Key} focused={focused?.GetType().Name ?? "null"}");
        if (focused is TextBox or NumberBox or AutoSuggestBox || IsInside(focused, Sidebar)) return;

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
            case VirtualKey.S when ctrl && shift: Vm.ToggleSidebar(SidebarKind.Settings); break;
            case VirtualKey.M when ctrl && shift: Window?.ToggleCompactMode(); break;
            case (VirtualKey)0xBC when ctrl: _ = Window?.ShowPreferencesAsync(); break;   // Ctrl+,
            case VirtualKey.F11: Vm.ToggleFullscreen(); break;
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

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            var items = await e.DataView.GetStorageItemsAsync();
            var paths = items.OfType<StorageFile>().Select(f => f.Path).ToList();
            if (paths.Count == 1 && IsSubtitle(paths[0]) && !Vm.Idle)
                Vm.AddSubtitle(paths[0]);
            else if (paths.Count > 0)
                Vm.OpenMany(paths, append: Vm.Sidebar == SidebarKind.Playlist);
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
