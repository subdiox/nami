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
    private PlayerViewModel Vm => App.Vm;
    private bool _syncing;
    private bool _scrubbing;
    private bool _showRemaining;

    public event Action? PipRequested;

    public Osc()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            SyncAll();
        };
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmChanged;

        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => _scrubbing = true), true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => { _scrubbing = false; SyncTime(); }), true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => { _scrubbing = false; SyncTime(); }), true);
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
            case nameof(PlayerViewModel.Fullscreen):
                FullscreenIcon.Glyph = Vm.Fullscreen ? "" : "";
                break;
        }
    }

    private void SyncAll()
    {
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
    private void LeftArrowButton_Click(object sender, RoutedEventArgs e) => Vm.SpeedStep(faster: false);
    private void RightArrowButton_Click(object sender, RoutedEventArgs e) => Vm.SpeedStep(faster: true);
    private void MuteButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleMute();
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Settings);
    private void PlaylistButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Playlist);
    private void PipButton_Click(object sender, RoutedEventArgs e) => PipRequested?.Invoke();
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
        Vm.SeekAbsolute(e.NewValue * Vm.Duration, exact: _scrubbing);
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
