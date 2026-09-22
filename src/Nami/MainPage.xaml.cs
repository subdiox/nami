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
    private readonly CursorVisibility _cursor = new();
    public Controls.VideoView VideoView => Video;
    private MainWindow? Window => Vm.Window;

    private static readonly TimeSpan HideDelay = TimeSpan.FromMilliseconds(2500);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _hideTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _clickTimer;
    private bool _overlayVisible = true;
    private bool _pointerOverControls;
    private bool _pointerInside;

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
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmChanged;

        // Pointer input on the video surface
        Root.PointerMoved += OnPointerMoved;
        Root.PointerPressed += OnPointerPressed;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerExited += (_, _) => { _pointerInside = false; if (!_dragging) _leftDown = false; };
        Root.PointerCaptureLost += (_, e) => EndDrag(e.Pointer);
        Root.PointerCanceled += (_, e) => EndDrag(e.Pointer);
        Root.PointerEntered += (_, _) => _pointerInside = true;
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
        Osc.MusicModeRequested += () => Window?.SetMusicMode(true);
        ApplyOscLayout(Vm.Services.Settings.OscLayout);

        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
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
        _cursor.Show();
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

    private void TryHideOverlay()
    {
        if (_pointerOverControls || Vm.Paused || Vm.Idle || Vm.MusicMode) return;
        if (!_overlayVisible) return;
        _overlayVisible = false;
        Fade(Osc, 0);
        Fade(BottomShade, 0);
        Fade(Mini, 0);
        Window?.SetTitleOverlayVisible(false);
        if (_pointerInside) _cursor.Hide();
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
        _pointerInside = true;
        ShowOverlay();

        if (!_leftDown || Vm.Fullscreen || Window is not { } w) return;
        var p = e.GetCurrentPoint(Root).Position;

        if (!_dragging)
        {
            if (Math.Abs(p.X - _pressPoint.X) <= DragThreshold && Math.Abs(p.Y - _pressPoint.Y) <= DragThreshold) return;
            if (w.IsMaximized) { _leftDown = false; return; }   // Windows does not drag maximized windows either
            _dragging = true;
            _clickTimer.Stop();
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
        menu.Items.Add(new MenuFlyoutItem { Text = Vm.Paused ? L.T("Play") : L.T("Pause"), Command = new Cmd(Vm.TogglePause) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Open file…"), Command = new Cmd(OpenFiles) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Open URL…"), Command = new Cmd(() => _ = OpenUrlAsync()) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Open in new window…"), Command = new Cmd(OpenFilesInNewWindow) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("New window"), Command = new Cmd(() => Vm.Services.Windows.New()) });
        var recent = new MenuFlyoutSubItem { Text = L.T("Recent files") };
        foreach (var h in Vm.History.Entries.Take(12))
            recent.Items.Add(new MenuFlyoutItem { Text = h.Display, Command = new Cmd(() => Vm.Open(h.Path)) });
        recent.IsEnabled = recent.Items.Count > 0;
        menu.Items.Add(recent);
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Add subtitle file…"), Command = new Cmd(async () =>
        {
            var f = await Controls.Sidebar.PickFilesAsync(Window, [".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup"], multiple: false);
            if (f.Count > 0) Vm.AddSubtitle(f[0]);
        }) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Quick settings"), Command = new Cmd(() => Vm.ToggleSidebar(SidebarKind.Settings)) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Playlist"), Command = new Cmd(() => Vm.ToggleSidebar(SidebarKind.Playlist)) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new ToggleMenuFlyoutItem { Text = L.T("Full screen"), IsChecked = Vm.Fullscreen, Command = new Cmd(Vm.ToggleFullscreen) });
        menu.Items.Add(new ToggleMenuFlyoutItem { Text = L.T("Always on top"), IsChecked = Vm.OnTop, Command = new Cmd(Vm.ToggleOnTop) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Mini player"), Command = new Cmd(() => Window?.ToggleCompactMode()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Screenshot"), Command = new Cmd(Vm.Screenshot) });
        menu.Items.Add(new MenuFlyoutItem { Text = double.IsNaN(Vm.AbLoopA) ? L.T("A-B loop: set point A") : double.IsNaN(Vm.AbLoopB) ? L.T("A-B loop: set point B") : L.T("Clear A-B loop"), Command = new Cmd(Vm.CycleAbLoop) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Frame step"), Command = new Cmd(Vm.FrameStep) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Frame back step"), Command = new Cmd(Vm.FrameBackStep) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Media info…"), Command = new Cmd(() => _ = new InspectorDialog(Vm) { XamlRoot = XamlRoot }.ShowAsync()) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Key bindings…"), Command = new Cmd(() => _ = new KeyBindingsDialog(Vm) { XamlRoot = XamlRoot }.ShowAsync()) });
        menu.Items.Add(new MenuFlyoutItem { Text = L.T("Preferences…"), Command = new Cmd(() => Window?.ShowPreferencesAsync()) });
        menu.ShowAt(Root, e.GetPosition(Root));
        e.Handled = true;
    }

    public async Task OpenUrlAsync()
    {
        var dlg = new OpenUrlDialog { XamlRoot = XamlRoot };
        var result = await dlg.ShowAsync();
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

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        // Let text boxes and the sidebar handle their own keys.
        var focused = FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
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
            case VirtualKey.I when ctrl: _ = new InspectorDialog(Vm) { XamlRoot = XamlRoot }.ShowAsync(); break;
            case VirtualKey.K when ctrl && shift: _ = new KeyBindingsDialog(Vm) { XamlRoot = XamlRoot }.ShowAsync(); break;
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

    /// <summary>Tiny ICommand wrapper for menu items.</summary>
    private sealed class Cmd(Action action) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }
}
