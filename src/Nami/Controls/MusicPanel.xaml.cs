using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Nami.Player;

namespace Nami.Controls;

/// <summary>IINA-style music mode controls, shown below the cover art.</summary>
public sealed partial class MusicPanel : UserControl
{
    public PlayerViewModel Vm { get; set; } = null!;
    private bool _syncing = true;

    public MusicPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => { Vm.PropertyChanged += OnVmChanged; SyncAll(); Services.L.Localize(this); };
        Unloaded += (_, _) => Vm.PropertyChanged -= OnVmChanged;
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Paused):
            case nameof(PlayerViewModel.Idle):
                PlayIcon.Glyph = Vm.Paused || Vm.Idle ? "" : "";
                break;
            case nameof(PlayerViewModel.TimePos):
            case nameof(PlayerViewModel.Duration):
                SyncTime();
                break;
            case nameof(PlayerViewModel.MediaTitle):
            case nameof(PlayerViewModel.MetaTitle):
            case nameof(PlayerViewModel.MetaArtist):
            case nameof(PlayerViewModel.MetaAlbum):
                SyncMeta();
                break;
            case nameof(PlayerViewModel.Muted):
            case nameof(PlayerViewModel.Volume):
                VolumeIcon.Glyph = Vm.Muted || Vm.Volume <= 0 ? "" : "";
                break;
            case nameof(PlayerViewModel.Shuffle):
                ShuffleIcon.Opacity = Vm.Shuffle ? 1 : 0.5;
                break;
            case nameof(PlayerViewModel.LoopPlaylist):
            case nameof(PlayerViewModel.LoopFile):
                LoopIcon.Opacity = Vm.LoopFile || Vm.LoopPlaylist ? 1 : 0.5;
                LoopIcon.Glyph = Vm.LoopFile ? "" : "";
                break;
        }
    }

    private void SyncAll()
    {
        foreach (var n in new[] { nameof(PlayerViewModel.Paused), nameof(PlayerViewModel.TimePos), nameof(PlayerViewModel.MetaTitle),
                     nameof(PlayerViewModel.Muted), nameof(PlayerViewModel.Shuffle), nameof(PlayerViewModel.LoopFile) })
            OnVmChanged(this, new PropertyChangedEventArgs(n));
    }

    private void SyncTime()
    {
        _syncing = true;
        try
        {
            TimeText.Text = Fmt.Time(Vm.TimePos);
            DurationText.Text = Fmt.Time(Vm.Duration);
            SeekSlider.Value = Vm.Duration > 0 ? Math.Clamp(Vm.TimePos / Vm.Duration, 0, 1) : 0;
        }
        finally { _syncing = false; }
    }

    private void SyncMeta()
    {
        TitleText.Text = string.IsNullOrEmpty(Vm.MetaTitle) ? Vm.MediaTitle : Vm.MetaTitle;
        string sub = Vm.MetaArtist;
        if (!string.IsNullOrEmpty(Vm.MetaAlbum)) sub = string.IsNullOrEmpty(sub) ? Vm.MetaAlbum : $"{sub} — {Vm.MetaAlbum}";
        ArtistText.Text = sub;
    }

    private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || Vm.Duration <= 0) return;
        Vm.SeekFromSlider(e.NewValue * Vm.Duration, exact: false);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Vm.TogglePause();
    private void Prev_Click(object sender, RoutedEventArgs e) => Vm.PlaylistPrev();
    private void Next_Click(object sender, RoutedEventArgs e) => Vm.PlaylistNext();
    private void Mute_Click(object sender, RoutedEventArgs e) => Vm.ToggleMute();
    private void Shuffle_Click(object sender, RoutedEventArgs e) => Vm.ToggleShuffle();
    private void Loop_Click(object sender, RoutedEventArgs e) => Vm.CycleLoop();
    private void PlaylistButton_Click(object sender, RoutedEventArgs e) => Vm.ToggleSidebar(SidebarKind.Playlist);
    private void ExitButton_Click(object sender, RoutedEventArgs e) => Vm.Window?.SetMusicMode(false);
}
