using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System.Runtime.InteropServices.WindowsRuntime;
using Nami.Player;

namespace Nami.Controls;

/// <summary>IINA-style floating on-screen controller.</summary>
public sealed partial class Osc : UserControl
{
    public PlayerViewModel Vm { get; set; } = null!;
    private bool _syncing = true;
    private bool _scrubbing;
    private bool _showRemaining;

    public event Action? PipRequested;
    /// <summary>Raised when one of the controller's menus opens / closes (the HUD stays while open).</summary>
    public event Action<bool>? MenuOpenChanged;

    private void ShowMenu(MenuFlyout menu, FrameworkElement at)
    {
        menu.Opened += (_, _) => MenuOpenChanged?.Invoke(true);
        menu.Closed += (_, _) => MenuOpenChanged?.Invoke(false);
        menu.ShowAt(at);
    }
    public event Action? MusicModeRequested;

    private Services.OscLayout _layout = Services.OscLayout.Floating;

    /// <summary>Show only the toolbar buttons the user picked ("more" is always there).</summary>
    public void ApplyToolbar(Services.OscToolbarItems items)
    {
        SpeedButton.Visibility = items.HasFlag(Services.OscToolbarItems.Speed) ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = items.HasFlag(Services.OscToolbarItems.Settings) ? Visibility.Visible : Visibility.Collapsed;
        PlaylistButton.Visibility = items.HasFlag(Services.OscToolbarItems.Playlist) ? Visibility.Visible : Visibility.Collapsed;
        MusicModeButton.Visibility = items.HasFlag(Services.OscToolbarItems.MusicMode) ? Visibility.Visible : Visibility.Collapsed;
        PipButton.Visibility = items.HasFlag(Services.OscToolbarItems.MiniPlayer) ? Visibility.Visible : Visibility.Collapsed;
        FullscreenButton.Visibility = items.HasFlag(Services.OscToolbarItems.Fullscreen) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly double[] Speeds = [0.5, 0.75, 1, 1.25, 1.5, 2];

    private void SyncSpeed() => SpeedText.Text = Vm.Speed == Math.Round(Vm.Speed) ? $"{Vm.Speed:0}×" : $"{Vm.Speed:0.##}×";

    private void SpeedButton_Click(object sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        foreach (var s in Speeds)
        {
            var item = new ToggleMenuFlyoutItem { Text = s == 1 ? Services.L.T("Normal") : $"{s:0.##}×", IsChecked = Math.Abs(Vm.Speed - s) < 0.001 };
            double speed = s;
            item.Click += (_, _) => Vm.SetSpeed(speed);
            menu.Items.Add(item);
        }
        ShowMenu(menu, SpeedButton);
    }

    private static MenuFlyoutItem Item(string text, Action action, string? accelerator = null)
    {
        var item = new MenuFlyoutItem { Text = text };
        if (accelerator is not null) item.KeyboardAcceleratorTextOverride = accelerator;
        item.Click += (_, _) => action();
        return item;
    }

    private static ToggleMenuFlyoutItem Toggle(string text, bool isChecked, Action action)
    {
        var item = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked };
        item.Click += (_, _) => action();
        return item;
    }

    private static async Task CheckUpdatesFromMenuAsync(MainPage page)
    {
        string? result = await page.CheckForUpdatesAsync(manual: true);
        if (result is not null) page.Vm.ShowOsd(Player.OsdMessage.IconInfo, result);
    }

    /// <summary>Secondary commands, where Windows apps keep them: a "…" menu on the command bar.</summary>
    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        var L = Services.L.T;
        var page = Vm.Window?.Page;
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        menu.Items.Add(Item(L("Open file…"), () => page?.OpenFiles(), "Ctrl+O"));
        menu.Items.Add(Item(L("Open URL…"), () => { if (page is not null) _ = page.OpenUrlAsync(); }, "Ctrl+U"));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Toggle(L("Always on top"), Vm.OnTop, Vm.ToggleOnTop));
        menu.Items.Add(Item(L("Music mode"), () => MusicModeRequested?.Invoke()));
        menu.Items.Add(Item(L("Screenshot"), Vm.Screenshot, Vm.KeyFor("screenshot")));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item(L("Media info…"), () => page?.ShowMediaInfo(), "Ctrl+I"));
        menu.Items.Add(Item(L("Key bindings…"), () => page?.ShowKeyBindings(), "Ctrl+Shift+K"));
        menu.Items.Add(Item(L("Check for updates…"), () => { if (page is not null) _ = CheckUpdatesFromMenuAsync(page); }));
        menu.Items.Add(Item(L("Preferences…"), () => { if (Vm.Window is { } w) _ = w.ShowPreferencesAsync(); }, "Ctrl+,"));
        menu.Closed += (_, _) => page?.FocusVideo();
        ShowMenu(menu, MoreButton);
    }

    /// <summary>Floating (rounded panel, two rows) or a full-width bar (one row).</summary>
    public Services.OscLayout Layout
    {
        get => _layout;
        set
        {
            if (_layout == value) return;
            _layout = value;
            ApplyLayout();
        }
    }

    private void Detach(FrameworkElement el)
    {
        // Parent may be null before the control is in a live tree, so try every container.
        FloatingRow1.Children.Remove(el);
        FloatingRow2.Children.Remove(el);
        BarRow.Children.Remove(el);
    }

    private void ApplyLayout()
    {
        foreach (var el in new FrameworkElement[] { VolumePanel, CenterPanel, ToolbarPanel, TimeText, SeekHost, DurationButton })
            Detach(el);

        if (_layout == Services.OscLayout.Floating)
        {
            FloatingRow1.Children.Add(VolumePanel); Grid.SetColumn(VolumePanel, 0);
            FloatingRow1.Children.Add(CenterPanel); Grid.SetColumn(CenterPanel, 1);
            FloatingRow1.Children.Add(ToolbarPanel); Grid.SetColumn(ToolbarPanel, 2);
            FloatingRow2.Children.Add(TimeText); Grid.SetColumn(TimeText, 0);
            FloatingRow2.Children.Add(SeekHost); Grid.SetColumn(SeekHost, 1);
            FloatingRow2.Children.Add(DurationButton); Grid.SetColumn(DurationButton, 2);
            FloatingRoot.Visibility = Visibility.Visible;
            BarRoot.Visibility = Visibility.Collapsed;
        }
        else
        {
            BarRow.Children.Add(CenterPanel); Grid.SetColumn(CenterPanel, 0);
            BarRow.Children.Add(TimeText); Grid.SetColumn(TimeText, 1);
            BarRow.Children.Add(SeekHost); Grid.SetColumn(SeekHost, 2);
            BarRow.Children.Add(DurationButton); Grid.SetColumn(DurationButton, 3);
            BarRow.Children.Add(VolumePanel); Grid.SetColumn(VolumePanel, 4);
            BarRow.Children.Add(ToolbarPanel); Grid.SetColumn(ToolbarPanel, 5);
            BarRoot.BorderThickness = _layout == Services.OscLayout.Top ? new Thickness(0, 0, 0, 1) : new Thickness(0, 1, 0, 0);
            FloatingRoot.Visibility = Visibility.Collapsed;
            BarRoot.Visibility = Visibility.Visible;
        }
    }

    public Osc()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            SyncAll();
            Services.L.Localize(this);
        };
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmChanged;

        SeekSlider.SizeChanged += (_, _) => SyncLoopMarkers();
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _scrubbing = false; SyncTime(); }), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _scrubbing = false; SyncTime(); }), true);
        // Wheel over the seek bar nudges by one second (Shift: 0.1 s, exact) for adjustments finer than a pixel.
        SeekSlider.PointerWheelChanged += (_, e) =>
        {
            if (Vm.Duration <= 0) return;
            int notches = e.GetCurrentPoint(SeekSlider).Properties.MouseWheelDelta / 120;
            if (notches == 0) return;
            bool shift = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            double step = shift ? 0.1 : 1.0;
            Vm.SeekFromSlider(Math.Clamp(Vm.TimePos + notches * step, 0, Vm.Duration), exact: true);
            e.Handled = true;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Paused):
            case nameof(PlayerViewModel.Idle):
                SyncPlayIcon();
                break;
            case nameof(PlayerViewModel.TimePos):
            case nameof(PlayerViewModel.Duration):
                SyncTime();
                break;
            case nameof(PlayerViewModel.Volume):
            case nameof(PlayerViewModel.Muted):
                SyncVolume();
                break;
            case nameof(PlayerViewModel.AbLoopA):
            case nameof(PlayerViewModel.AbLoopB):
                SyncLoopMarkers();
                break;
            case nameof(PlayerViewModel.Speed):
                SyncSpeed();
                break;
            case nameof(PlayerViewModel.Fullscreen):
                FullscreenIcon.Glyph = Vm.Fullscreen ? "" : "";
                break;
        }
    }

    private void SyncLoopMarkers()
    {
        double w = SeekSlider.ActualWidth;
        Place(LoopA, Vm.AbLoopA, w);
        Place(LoopB, Vm.AbLoopB, w);

        void Place(Microsoft.UI.Xaml.Shapes.Rectangle r, double t, double width)
        {
            if (double.IsNaN(t) || Vm.Duration <= 0 || width <= 0) { r.Visibility = Visibility.Collapsed; return; }
            r.Margin = new Thickness(Math.Clamp(t / Vm.Duration, 0, 1) * width - 1, 0, 0, 0);
            r.Visibility = Visibility.Visible;
        }
    }

    private void SyncAll()
    {
        SyncSpeed();
        SyncPlayIcon();
        SyncTime();
        SyncVolume();
        FullscreenIcon.Glyph = Vm.Fullscreen ? "" : "";
    }

    private void SyncPlayIcon() => PlayIcon.Glyph = Vm.Paused || Vm.Idle ? "" : "";

    private void SyncTime()
    {
        _syncing = true;
        try
        {
            double dur = Vm.Duration;
            TimeText.Text = Fmt.Time(Vm.TimePos);
            DurationText.Text = _showRemaining && dur > 0 ? "-" + Fmt.Time(Math.Max(0, dur - Vm.TimePos)) : Fmt.Time(dur);
            if (!_scrubbing)
                SeekSlider.Value = dur > 0 ? Math.Clamp(Vm.TimePos / dur, 0, 1) : 0;
            SeekSlider.IsEnabled = dur > 0;
        }
        finally { _syncing = false; }
    }

    private void SyncVolume()
    {
        _syncing = true;
        try
        {
            VolumeSlider.Value = Vm.Volume;
            double v = Vm.Volume;
            VolumeIcon.Glyph = Vm.Muted || v <= 0 ? ""
                : v < 34 ? ""
                : v < 67 ? ""
                : "";
        }
        finally { _syncing = false; }
    }

    // ---- handlers ------------------------------------------------------------------

    private void PlayButton_Click(object sender, RoutedEventArgs e) => Vm.TogglePause();
    private void PrevButton_Click(object sender, RoutedEventArgs e) => Vm.PlaylistPrev();
    private void NextButton_Click(object sender, RoutedEventArgs e) => Vm.PlaylistNext();
    private void MuteButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleMute();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Settings);
    private void PlaylistButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Playlist);
    private void PipButton_Click(object sender, RoutedEventArgs e) => PipRequested?.Invoke();
    private void MusicModeButton_Click(object sender, RoutedEventArgs e) => MusicModeRequested?.Invoke();
    private void FullscreenButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleFullscreen();

    private void DurationButton_Click(object sender, RoutedEventArgs e)
    {
        _showRemaining = !_showRemaining;
        SyncTime();
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetVolume(e.NewValue);
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || Vm.Duration <= 0) return;
        Vm.SeekFromSlider(e.NewValue * Vm.Duration, exact: _scrubbing);
    }

    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _thumbBitmap;
    private int _thumbIndex = -1;
    private ThumbnailSet? _thumbSet;

    private void SeekSlider_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (Vm.Duration <= 0) { SeekTip.Visibility = Visibility.Collapsed; return; }
        var p = e.GetCurrentPoint(SeekSlider).Position;
        double frac = Math.Clamp(p.X / Math.Max(1, SeekSlider.ActualWidth), 0, 1);
        double t = frac * Vm.Duration;
        SeekTipText.Text = Fmt.Time(t);
        UpdateThumbnail(t);
        SeekTip.Visibility = Visibility.Visible;
        SeekTip.UpdateLayout();
        var sliderPos = SeekSlider.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(p.X, 0));
        SeekTip.Margin = new Thickness(Math.Max(0, sliderPos.X - SeekTip.ActualWidth / 2), -(SeekTip.ActualHeight + 8), 0, 0);
    }

    private void UpdateThumbnail(double time)
    {
        var set = Vm.Thumbnails.Current;
        if (set is null || set.Path != Vm.FilePath)
        {
            SeekThumb.Visibility = Visibility.Collapsed;
            _thumbSet = null;
            _thumbIndex = -1;
            return;
        }
        var frame = set.Get(time);
        if (frame is null) { SeekThumb.Visibility = Visibility.Collapsed; return; }
        var (index, bgra) = frame.Value;
        if (_thumbSet != set || _thumbBitmap is null)
        {
            _thumbBitmap = new Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap(set.Width, set.Height);
            _thumbSet = set;
            _thumbIndex = -1;
        }
        if (index != _thumbIndex)
        {
            using (var stream = _thumbBitmap.PixelBuffer.AsStream())
            {
                stream.Position = 0;
                stream.Write(bgra, 0, bgra.Length);
            }
            _thumbBitmap.Invalidate();
            _thumbIndex = index;
        }
        SeekThumb.Source = _thumbBitmap;
        SeekThumb.Height = 160.0 * set.Height / set.Width;
        SeekThumb.Visibility = Visibility.Visible;
    }

    private void SeekSlider_PointerExited(object sender, PointerRoutedEventArgs e) => SeekTip.Visibility = Visibility.Collapsed;
}
