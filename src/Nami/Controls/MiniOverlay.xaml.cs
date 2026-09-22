using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Nami.Player;

namespace Nami.Controls;

/// <summary>Minimal controls for the mini player (IINA's picture-in-picture look).</summary>
public sealed partial class MiniOverlay : UserControl
{
    public PlayerViewModel Vm { get; set; } = null!;
    private bool _syncing = true;
    private bool _scrubbing;

    public event Action? ExitRequested;
    public event Action? CloseRequested;

    public MiniOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            SyncPlay();
            SyncTime();
            Services.L.Localize(this);
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
                SyncPlay();
                break;
            case nameof(PlayerViewModel.TimePos):
            case nameof(PlayerViewModel.Duration):
                SyncTime();
                break;
        }
    }

    private void SyncPlay() => PlayIcon.Glyph = Vm.Paused || Vm.Idle ? "" : "";

    private void SyncTime()
    {
        _syncing = true;
        try
        {
            if (!_scrubbing) SeekSlider.Value = Vm.Duration > 0 ? Math.Clamp(Vm.TimePos / Vm.Duration, 0, 1) : 0;
            SeekSlider.IsEnabled = Vm.Duration > 0;
        }
        finally { _syncing = false; }
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) => Vm.TogglePause();
    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || Vm.Duration <= 0) return;
        Vm.SeekFromSlider(e.NewValue * Vm.Duration, exact: _scrubbing);
    }
}
