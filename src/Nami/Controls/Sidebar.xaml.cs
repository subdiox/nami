using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Nami.Player;
using Nami.Services;
using Windows.Storage.Pickers;
using Windows.System;

namespace Nami.Controls;

/// <summary>IINA-style right sidebar: Quick Settings (video / audio / subtitles) or Playlist (playlist / chapters).</summary>
public sealed partial class Sidebar : UserControl
{
    private PlayerViewModel Vm => App.Vm;
    private bool _syncing;
    private SidebarKind _kind = SidebarKind.None;

    private static readonly string[] AspectValues = ["no", "4:3", "16:9", "16:10", "21:9", "1:1"];

    public Sidebar()
    {
        InitializeComponent();
        VideoTrackList.ItemsSource = Vm.VideoTracks;
        AudioTrackList.ItemsSource = Vm.AudioTracks;
        SubTrackList.ItemsSource = Vm.SubTracks;
        SecondarySubTrackList.ItemsSource = Vm.SubTracks;
        PlaylistList.ItemsSource = Vm.Playlist;
        ChapterList.ItemsSource = Vm.Chapters;
        HistoryList.ItemsSource = Vm.History.Entries;

        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            Vm.Playlist.CollectionChanged += OnListsChanged;
            Vm.Chapters.CollectionChanged += OnListsChanged;
            Vm.History.Entries.CollectionChanged += OnListsChanged;
            SyncAll();
        };
        Unloaded += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            Vm.Playlist.CollectionChanged -= OnListsChanged;
            Vm.Chapters.CollectionChanged -= OnListsChanged;
            Vm.History.Entries.CollectionChanged -= OnListsChanged;
        };
    }

    /// <summary>Switch the sidebar to the given mode (rebuilds the tab strip).</summary>
    public void Show(SidebarKind kind)
    {
        if (_kind == kind) return;
        _kind = kind;
        bool settings = kind == SidebarKind.Settings;
        foreach (var item in new[] { TabVideo, TabAudio, TabSub })
            item.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in new[] { TabPlaylist, TabChapters, TabHistory })
            item.Visibility = settings ? Visibility.Collapsed : Visibility.Visible;
        Tabs.SelectedItem = settings
            ? new[] { TabVideo, TabAudio, TabSub }[Math.Clamp(Vm.SettingsTab, 0, 2)]
            : new[] { TabPlaylist, TabChapters, TabHistory }[Math.Clamp(Vm.PlaylistTab, 0, 2)];
        UpdatePanels();
    }

    private void Tabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is null) return;
        if (_kind == SidebarKind.Settings) Vm.SettingsTab = Array.IndexOf(new[] { TabVideo, TabAudio, TabSub }, sender.SelectedItem);
        else if (_kind == SidebarKind.Playlist) Vm.PlaylistTab = Array.IndexOf(new[] { TabPlaylist, TabChapters, TabHistory }, sender.SelectedItem);
        UpdatePanels();
    }

    private void UpdatePanels()
    {
        string tag = (Tabs.SelectedItem?.Tag as string) ?? "";
        VideoPanel.Visibility = tag == "video" ? Visibility.Visible : Visibility.Collapsed;
        AudioPanel.Visibility = tag == "audio" ? Visibility.Visible : Visibility.Collapsed;
        SubPanel.Visibility = tag == "sub" ? Visibility.Visible : Visibility.Collapsed;
        PlaylistPanel.Visibility = tag == "playlist" ? Visibility.Visible : Visibility.Collapsed;
        ChaptersPanel.Visibility = tag == "chapters" ? Visibility.Visible : Visibility.Collapsed;
        HistoryPanel.Visibility = tag == "history" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "video") RefreshHdrInfo();
    }

    // ---- sync from view model ---------------------------------------------------------

    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaylistEmptyText.Visibility = Vm.Playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChaptersEmptyText.Visibility = Vm.Chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistCountText.Text = Vm.Playlist.Count == 0 ? "" : $"{Vm.Playlist.Count} 項目";
        HistoryEmptyText.Visibility = Vm.History.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = Vm.History.Entries.Count == 0 ? "" : $"{Vm.History.Entries.Count} 件";
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        _syncing = true;
        try
        {
            switch (e.PropertyName)
            {
                case nameof(PlayerViewModel.Aspect):
                    AspectButtons.SelectedIndex = Math.Max(0, Array.IndexOf(AspectValues, NormalizeAspect(Vm.Aspect)));
                    break;
                case nameof(PlayerViewModel.Rotate):
                    RotateButtons.SelectedIndex = (int)(((Vm.Rotate % 360) + 360) % 360) / 90;
                    break;
                case nameof(PlayerViewModel.Speed):
                    SpeedSlider.Value = Math.Clamp(Vm.Speed, SpeedSlider.Minimum, SpeedSlider.Maximum);
                    SpeedLabel.Text = Fmt.Speed(Vm.Speed);
                    break;
                case nameof(PlayerViewModel.Deinterlace): DeinterlaceSwitch.IsOn = Vm.Deinterlace; break;
                case nameof(PlayerViewModel.Brightness): BrightnessSlider.Value = Vm.Brightness; break;
                case nameof(PlayerViewModel.Contrast): ContrastSlider.Value = Vm.Contrast; break;
                case nameof(PlayerViewModel.Saturation): SaturationSlider.Value = Vm.Saturation; break;
                case nameof(PlayerViewModel.Gamma): GammaSlider.Value = Vm.Gamma; break;
                case nameof(PlayerViewModel.Hue): HueSlider.Value = Vm.Hue; break;
                case nameof(PlayerViewModel.Volume):
                    VolumeSlider.Value = Vm.Volume;
                    VolumeLabel.Text = $"{Vm.Volume:0}%";
                    break;
                case nameof(PlayerViewModel.AudioDelay):
                    AudioDelaySlider.Value = Math.Clamp(Vm.AudioDelay, -5, 5);
                    AudioDelayLabel.Text = $"{Vm.AudioDelay:+0.00;-0.00;0.00} s";
                    break;
                case nameof(PlayerViewModel.SubDelay):
                    SubDelaySlider.Value = Math.Clamp(Vm.SubDelay, -10, 10);
                    SubDelayLabel.Text = $"{Vm.SubDelay:+0.0;-0.0;0.0} s";
                    break;
                case nameof(PlayerViewModel.SubScale):
                    SubScaleSlider.Value = Vm.SubScale;
                    SubScaleLabel.Text = $"{Vm.SubScale * 100:0}%";
                    break;
                case nameof(PlayerViewModel.SubPos):
                    SubPosSlider.Value = Vm.SubPos;
                    SubPosLabel.Text = Vm.SubPos.ToString();
                    break;
                case nameof(PlayerViewModel.HwdecCurrent):
                case nameof(PlayerViewModel.FilePath):
                    RefreshHdrInfo();
                    break;
            }
        }
        finally { _syncing = false; }
    }

    private void SyncAll()
    {
        foreach (var name in new[]
                 {
                     nameof(PlayerViewModel.Aspect), nameof(PlayerViewModel.Rotate), nameof(PlayerViewModel.Speed),
                     nameof(PlayerViewModel.Deinterlace), nameof(PlayerViewModel.Brightness), nameof(PlayerViewModel.Contrast),
                     nameof(PlayerViewModel.Saturation), nameof(PlayerViewModel.Gamma), nameof(PlayerViewModel.Hue),
                     nameof(PlayerViewModel.Volume), nameof(PlayerViewModel.AudioDelay), nameof(PlayerViewModel.SubDelay),
                     nameof(PlayerViewModel.SubScale), nameof(PlayerViewModel.SubPos),
                 })
            OnVmChanged(this, new PropertyChangedEventArgs(name));
        OnListsChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        _syncing = true;
        HdrCombo.SelectedIndex = (int)App.Settings.HdrMode;
        _syncing = false;
        RefreshHdrInfo();
    }

    private static string NormalizeAspect(string v) => v switch
    {
        "no" or "-1" or "-1.000000" => "no",
        _ => v,
    };

    private void RefreshHdrInfo()
    {
        var d = HdrController.LastDisplay;
        string mode = HdrController.IsPassthroughActive ? "HDR10 パススルー" : "SDR";
        HdrInfo.Text = d is null
            ? $"出力: {mode}"
            : $"出力: {mode} / ディスプレイ: {(d.IsHdr ? "HDR" : "SDR")} {d.BitsPerColor}bit, ピーク {d.MaxLuminance:0} nits, SDR 白 {d.SdrWhiteNits:0} nits";
        DecoderInfo.Text = string.IsNullOrEmpty(Vm.HwdecCurrent) || Vm.HwdecCurrent == "no"
            ? "デコード: ソフトウェア"
            : $"デコード: {Vm.HwdecCurrent}";
    }

    // ---- video -----------------------------------------------------------------------

    private void VideoTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.SetTrack("vid", t.Id);
    }

    private void AspectButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AspectButtons.SelectedIndex < 0) return;
        string v = AspectValues[AspectButtons.SelectedIndex];
        if (v == NormalizeAspect(Vm.Aspect)) return;
        Vm.SetAspect(v);
    }

    private void RotateButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || RotateButtons.SelectedIndex < 0) return;
        if (RotateButtons.SelectedIndex * 90 == ((Vm.Rotate % 360) + 360) % 360) return;
        Vm.SetRotate(RotateButtons.SelectedIndex * 90);
    }

    private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetSpeed(Math.Round(e.NewValue, 2));
    }

    private void SpeedPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string s } && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            Vm.SetSpeed(v);
    }

    private void DeinterlaceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetDeinterlace(DeinterlaceSwitch.IsOn);
    }

    private void EqSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing || sender is not Slider { Tag: string name }) return;
        Vm.SetEq(name, (long)Math.Round(e.NewValue));
    }

    private void ResetEq_Click(object sender, RoutedEventArgs e) => Vm.ResetEq();

    private void HdrCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HdrCombo.SelectedIndex < 0) return;
        App.Settings.HdrMode = (HdrMode)HdrCombo.SelectedIndex;
        App.Settings.Save();
        App.Window?.ApplyHdr();
        RefreshHdrInfo();
    }

    // ---- audio -----------------------------------------------------------------------

    private void AudioTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.SetTrack("aid", t.Id);
    }

    private void AudioOff_Click(object sender, RoutedEventArgs e) => Vm.SetTrackOff("aid");

    private async void AddAudio_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".mka", ".mp3", ".aac", ".flac", ".m4a", ".ac3", ".dts", ".opus", ".ogg", ".wav"]);
        if (file is not null) Vm.AddAudio(file);
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetVolume(e.NewValue);
    }

    private void AudioDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetAudioDelay(Math.Round(e.NewValue, 2));
    }

    private void ResetAudioDelay_Click(object sender, RoutedEventArgs e) => Vm.SetAudioDelay(0);

    // ---- subtitles -------------------------------------------------------------------

    private void SubTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.SetTrack("sid", t.Id);
    }

    private void SecondarySubTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.SetTrack("secondary-sid", t.Id);
    }

    private void SubOff_Click(object sender, RoutedEventArgs e) => Vm.SetTrackOff("sid");
    private void SecondarySubOff_Click(object sender, RoutedEventArgs e) => Vm.SetTrackOff("secondary-sid");

    private async void AddSub_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup", ".idx", ".txt"]);
        if (file is not null) Vm.AddSubtitle(file);
    }

    private void SubDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetSubDelay(Math.Round(e.NewValue, 2));
    }

    private void ResetSubDelay_Click(object sender, RoutedEventArgs e) => Vm.SetSubDelay(0);

    private void SubScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetSubScale(Math.Round(e.NewValue, 2));
    }

    private void SubPosSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetSubPos((long)Math.Round(e.NewValue));
    }

    // ---- playlist --------------------------------------------------------------------

    private void PlaylistList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaylistItem item) Vm.PlaylistPlayIndex(item.Index);
    }

    private void PlaylistList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is PlaylistItem item)
            PlaylistList.SelectedItem = item;
    }

    private void PlaylistList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && PlaylistList.SelectedItem is PlaylistItem item)
        {
            Vm.PlaylistRemove(item.Index);
            e.Handled = true;
        }
    }

    private void PlaylistPlay_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistItem item) Vm.PlaylistPlayIndex(item.Index);
    }

    private void PlaylistRemove_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistItem item) Vm.PlaylistRemove(item.Index);
    }

    private async void PlaylistReveal_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistList.SelectedItem is PlaylistItem item && File.Exists(item.Filename))
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(item.Filename)!);
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(item.Filename);
            var options = new Windows.System.FolderLauncherOptions();
            options.ItemsToSelect.Add(file);
            await Launcher.LaunchFolderAsync(folder, options);
        }
    }

    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var files = await PickFilesAsync(FileAssociation.AllExtensions);
        if (files.Count > 0) Vm.OpenMany(files, append: true);
    }

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e) => Vm.PlaylistClear();

    // ---- history ---------------------------------------------------------------------

    private void HistoryList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is HistoryEntry h) Vm.Open(h.Path);
    }

    private void HistoryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is HistoryEntry h) HistoryList.SelectedItem = h;
    }

    private void HistoryPlay_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry h) Vm.Open(h.Path);
    }

    private void HistoryAppend_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry h) Vm.Open(h.Path, append: true);
    }

    private void HistoryRemove_Click(object sender, RoutedEventArgs e)
    {
        if (HistoryList.SelectedItem is HistoryEntry h) Vm.History.Remove(h);
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e) => Vm.History.Clear();

    private void ChapterList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ChapterInfo c) Vm.SetChapter(c.Index);
    }

    // ---- pickers ---------------------------------------------------------------------

    private static async Task<string?> PickFileAsync(IEnumerable<string> extensions)
    {
        var files = await PickFilesAsync(extensions, multiple: false);
        return files.Count > 0 ? files[0] : null;
    }

    public static async Task<List<string>> PickFilesAsync(IEnumerable<string> extensions, bool multiple = true)
    {
        var result = new List<string>();
        if (App.Window is null) return result;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.Window));
        if (multiple)
        {
            var files = await picker.PickMultipleFilesAsync();
            result.AddRange(files.Select(f => f.Path));
        }
        else
        {
            var file = await picker.PickSingleFileAsync();
            if (file is not null) result.Add(file.Path);
        }
        return result;
    }
}
