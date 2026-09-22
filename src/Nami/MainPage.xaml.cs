using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Nami.Mpv;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;

namespace Nami;

public sealed partial class MainPage : Page
{
    private MpvPlayer? _player;
    private double _duration;
    private bool _updatingSlider;
    private bool _paused = true;

    public MainPage()
    {
        InitializeComponent();
        Video.PlayerCreated += OnPlayerCreated;
        Loaded += (_, _) => Focus(FocusState.Programmatic);
        KeyDown += OnKeyDown;
        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    /// <summary>Open a file or URL as soon as the player exists.</summary>
    public void Open(string pathOrUrl) => Video.WhenReady(p => p.LoadFile(pathOrUrl));

    private void OnPlayerCreated(MpvPlayer player)
    {
        _player = player;
        player.ObserveFlag("pause");
        player.ObserveDouble("time-pos");
        player.ObserveDouble("duration");
        player.ObserveString("media-title");
        player.ObserveFlag("core-idle");
        player.PropertyChanged += OnMpvProperty;
        player.EndFile += e =>
        {
            if (e.IsError) StatusText.Text = $"Error: {LibMpv.ErrorString(e.ErrorCode)}";
        };
        StatusText.Text = $"mpv API {MpvPlayer.ApiVersion}";
    }

    private void OnMpvProperty(string name, object? value)
    {
        switch (name)
        {
            case "pause":
                _paused = value is true;
                PlayPauseIcon.Glyph = _paused ? "" : "";
                break;
            case "duration":
                _duration = value is double d ? d : 0;
                UpdateTime();
                break;
            case "time-pos":
                if (value is double t)
                {
                    UpdateTime(t);
                    if (_duration > 0)
                    {
                        _updatingSlider = true;
                        SeekSlider.Value = t / _duration * SeekSlider.Maximum;
                        _updatingSlider = false;
                    }
                }
                break;
            case "media-title":
                if (value is string title && App.Window is not null)
                    App.Window.Title = string.IsNullOrEmpty(title) ? "Nami" : $"{title} - Nami";
                break;
        }
    }

    private double _lastPos;

    private void UpdateTime(double? pos = null)
    {
        if (pos is double p) _lastPos = p;
        TimeText.Text = $"{Format(_lastPos)} / {Format(_duration)}";
    }

    private static string Format(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes}:{ts.Seconds:00}";
    }

    // ------------------------------------------------------------------ transport

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in new[] { ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".flv", ".wmv", ".mp3", ".flac", ".m4a", ".opus", ".ogg", ".wav" })
            picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");

        if (App.Window is null) return;
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is not null) Open(file.Path);
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _player?.TogglePause();

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _player is null || _duration <= 0) return;
        _player.Seek(e.NewValue / SeekSlider.Maximum * _duration, relative: false);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_player is null) return;
        bool handled = true;
        switch (e.Key)
        {
            case VirtualKey.Space:
            case VirtualKey.K:
                _player.TogglePause();
                break;
            case VirtualKey.Left:
                _player.Seek(-5);
                break;
            case VirtualKey.Right:
                _player.Seek(5);
                break;
            case VirtualKey.Up:
                _player.TryCommand("add", "volume", "5");
                break;
            case VirtualKey.Down:
                _player.TryCommand("add", "volume", "-5");
                break;
            case VirtualKey.M:
                _player.TryCommand("cycle", "mute");
                break;
            case VirtualKey.F:
                App.Window?.ToggleFullScreen();
                break;
            case VirtualKey.Escape:
                App.Window?.ExitFullScreen();
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    // ------------------------------------------------------------------ drag & drop

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        bool first = true;
        foreach (var item in items)
        {
            if (item is StorageFile f)
            {
                Video.WhenReady(p => p.LoadFile(f.Path, append: !first));
                first = false;
            }
        }
    }
}
