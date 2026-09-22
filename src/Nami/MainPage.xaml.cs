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
    private PlayerViewModel Vm => App.Vm;
    public Controls.VideoView VideoView => Video;

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

    public MainPage()
    {
        InitializeComponent();

        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.Interval = HideDelay;
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => TryHideOverlay();

        _clickTimer = DispatcherQueue.CreateTimer();
        _clickTimer.Interval = WindowInterop.DoubleClickTime;
        _clickTimer.IsRepeating = false;
        _clickTimer.Tick += (_, _) => Vm.TogglePause();

        Loaded += OnLoaded;
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmChanged;

        // Pointer input on the video surface
        Root.PointerMoved += OnPointerMoved;
        Root.PointerPressed += OnPointerPressed;
        Root.PointerReleased += OnPointerReleased;
        Root.PointerExited += (_, _) => { _pointerInside = false; _leftDown = false; };
        Root.PointerEntered += (_, _) => _pointerInside = true;
        Root.PointerWheelChanged += OnPointerWheel;
        Root.RightTapped += OnRightTapped;
        Surface.Tapped += OnVideoTapped;
        Surface.DoubleTapped += OnVideoDoubleTapped;

        foreach (var c in new UIElement[] { Osc, Sidebar })
        {
            c.PointerEntered += (_, _) => { _pointerOverControls = true; ShowOverlay(); };
            c.PointerExited += (_, _) => { _pointerOverControls = false; RestartHideTimer(); };
        }

        Osc.PipRequested += () => App.Window?.ToggleCompactMode();

        DragOver += OnDragOver;
        Drop += OnDrop;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus(FocusState.Programmatic);
        Vm.PropertyChanged += OnVmChanged;
        Vm.Error += msg => Vm.ShowText("エラー: " + msg, 4000);
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

    private void UpdateEmptyState()
        => EmptyState.Visibility = Vm.Idle && string.IsNullOrEmpty(Vm.FilePath) ? Visibility.Visible : Visibility.Collapsed;

    // ---- overlay auto-hide ------------------------------------------------------------

    public void ShowOverlay()
    {
        WindowInterop.ShowCursor();
        if (!_overlayVisible)
        {
            _overlayVisible = true;
            Fade(Osc, 1);
            Fade(BottomShade, 1);
            App.Window?.SetTitleOverlayVisible(true);
        }
        RestartHideTimer();
    }

    private void RestartHideTimer()
    {
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    private void TryHideOverlay()
    {
        if (_pointerOverControls || Vm.Paused || Vm.Idle) return;
        if (!_overlayVisible) return;
        _overlayVisible = false;
        Fade(Osc, 0);
        Fade(BottomShade, 0);
        App.Window?.SetTitleOverlayVisible(false);
        if (_pointerInside) WindowInterop.HideCursor();
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

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        _pointerInside = true;
        ShowOverlay();

        if (_leftDown && !_dragging && !Vm.Fullscreen && App.Window is { } w)
        {
            var p = e.GetCurrentPoint(Root).Position;
            if (Math.Abs(p.X - _pressPoint.X) > 4 || Math.Abs(p.Y - _pressPoint.Y) > 4)
            {
                _dragging = true;
                _clickTimer.Stop();
                WindowInterop.BeginWindowDrag(w.Hwnd);
                _leftDown = false;
            }
        }
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
        _leftDown = false;
    }

    private void OnVideoTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_dragging || e.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Pen) return;
        if (Vm.Sidebar != SidebarKind.None && e.PointerDeviceType != Microsoft.UI.Input.PointerDeviceType.Mouse)
            return;
        // Single click toggles pause, but only after the double-click window has passed.
        _clickTimer.Stop();
        _clickTimer.Start();
    }

    private void OnVideoDoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _clickTimer.Stop();
        Vm.ToggleFullscreen();
    }

    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (e.OriginalSource is not UIElement src || !IsVideoSurface(src)) return;
        var props = e.GetCurrentPoint(Root).Properties;
        int delta = props.MouseWheelDelta;
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
        menu.Items.Add(new MenuFlyoutItem { Text = Vm.Paused ? "再生" : "一時停止", Command = new Cmd(Vm.TogglePause) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "ファイルを開く…", Command = new Cmd(OpenFiles) });
        menu.Items.Add(new MenuFlyoutItem { Text = "字幕ファイルを追加…", Command = new Cmd(async () =>
        {
            var f = await Controls.Sidebar.PickFilesAsync([".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup"], multiple: false);
            if (f.Count > 0) Vm.AddSubtitle(f[0]);
        }) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "クイック設定", Command = new Cmd(() => Vm.ToggleSidebar(SidebarKind.Settings)) });
        menu.Items.Add(new MenuFlyoutItem { Text = "プレイリスト", Command = new Cmd(() => Vm.ToggleSidebar(SidebarKind.Playlist)) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new ToggleMenuFlyoutItem { Text = "全画面", IsChecked = Vm.Fullscreen, Command = new Cmd(Vm.ToggleFullscreen) });
        menu.Items.Add(new ToggleMenuFlyoutItem { Text = "常に手前に表示", IsChecked = Vm.OnTop, Command = new Cmd(Vm.ToggleOnTop) });
        menu.Items.Add(new MenuFlyoutItem { Text = "ミニプレイヤー", Command = new Cmd(() => App.Window?.ToggleCompactMode()) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "スクリーンショット", Command = new Cmd(Vm.Screenshot) });
        menu.Items.Add(new MenuFlyoutItem { Text = "環境設定…", Command = new Cmd(() => App.Window?.ShowPreferencesAsync()) });
        menu.ShowAt(Root, e.GetPosition(Root));
        e.Handled = true;
    }

    public async void OpenFiles()
    {
        var files = await Controls.Sidebar.PickFilesAsync(FileAssociation.AllExtensions);
        if (files.Count > 0) Vm.OpenMany(files);
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
            case VirtualKey.O when ctrl: OpenFiles(); break;
            case VirtualKey.P when ctrl && shift: Vm.ToggleSidebar(SidebarKind.Playlist); break;
            case VirtualKey.S when ctrl && shift: Vm.ToggleSidebar(SidebarKind.Settings); break;
            case VirtualKey.M when ctrl && shift: App.Window?.ToggleCompactMode(); break;
            case (VirtualKey)0xBC when ctrl: _ = App.Window?.ShowPreferencesAsync(); break;   // Ctrl+,
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
        e.DragUIOverride.Caption = "Nami で再生";
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
