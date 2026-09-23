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
    public PlayerViewModel Vm { get; private set; } = null!;
    // true until Bind(): value-changed handlers fire while XAML sets initial ranges, before a view model exists
    private bool _syncing = true;
    private SidebarKind _kind = SidebarKind.None;

    private static readonly string[] AspectValues = ["no", "4:3", "16:9", "16:10", "21:9", "1:1"];
    private static readonly string[] CropValues = ["", "16:9", "4:3", "1:1", "2.35:1"];
    private readonly Slider[] _eqSliders = new Slider[10];

    public Sidebar()
    {
        InitializeComponent();
        BuildEq();
        // List<object>: a List<string> projects as IVector<string>, which XAML cannot enumerate as items.
        AspectButtons.ItemsSource = new List<object> { L.T("Auto"), "4:3", "16:9", "16:10", "21:9", "1:1" };
        CropButtons.ItemsSource = new List<object> { L.T("None"), "16:9", "4:3", "1:1", "2.35:1" };
        RotateButtons.ItemsSource = new List<object> { "0°", "90°", "180°", "270°" };
        HdrCombo.ItemsSource = new List<object> { L.T("Auto (follow the display)"), L.T("Tone-map to SDR"), L.T("HDR passthrough") };
        SubTargetButtons.ItemsSource = new List<object> { L.T("Primary"), L.T("Secondary") };
    }

    /// <summary>Attach the sidebar to its window's player (called once by MainPage).</summary>
    public void Bind(PlayerViewModel vm)
    {
        Vm = vm;
        VideoTrackList.ItemsSource = Vm.VideoTracks;
        AudioTrackList.ItemsSource = Vm.AudioTracks;
        SubTrackList.ItemsSource = Vm.PrimarySubChoices;
        SecondarySubTrackList.ItemsSource = Vm.SecondarySubChoices;
        PlaylistList.ItemsSource = Vm.Playlist;
        ChapterList.ItemsSource = Vm.Chapters;
        HistoryList.ItemsSource = Vm.History.Entries;
        AudioDeviceCombo.ItemsSource = Vm.AudioDevices;
        _syncing = false;

        Loaded += (_, _) =>
        {
            Vm.PropertyChanged += OnVmChanged;
            Vm.Playlist.CollectionChanged += OnListsChanged;
            Vm.Chapters.CollectionChanged += OnListsChanged;
            Vm.History.Entries.CollectionChanged += OnListsChanged;
            Vm.AudioDevices.CollectionChanged += OnAudioDevicesChanged;
            SyncAll();
            L.Localize(this);
        };
        Unloaded += (_, _) =>
        {
            Vm.PropertyChanged -= OnVmChanged;
            Vm.Playlist.CollectionChanged -= OnListsChanged;
            Vm.Chapters.CollectionChanged -= OnListsChanged;
            Vm.History.Entries.CollectionChanged -= OnListsChanged;
            Vm.AudioDevices.CollectionChanged -= OnAudioDevicesChanged;
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

    private void Close_Click(object sender, RoutedEventArgs e) => Vm.CloseSidebar();

    /// <summary>
    /// Lay out every pane once while the sidebar is off screen. Realizing the templates of a pane
    /// (lists, sliders, combo boxes) costs a visible delay the first time it is shown; done here at
    /// startup instead of on the first click.
    /// </summary>
    public void Prewarm()
    {
        foreach (var panel in new[] { VideoPanel, AudioPanel, SubPanel, PlaylistPanel, ChaptersPanel, HistoryPanel })
        {
            panel.Visibility = Visibility.Visible;
            UpdateLayout();
        }
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

    private void OnAudioDevicesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The device list arrives after the first sync; re-select the current device once it exists.
        App.Log($"audio devices: {Vm.AudioDevices.Count}, current={Vm.AudioDevice}");
        _syncing = true;
        try { AudioDeviceCombo.SelectedItem = Vm.AudioDevices.FirstOrDefault(d => d.Name == Vm.AudioDevice); }
        finally { _syncing = false; }
    }

    private void OnListsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        PlaylistEmptyText.Visibility = Vm.Playlist.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ChaptersEmptyText.Visibility = Vm.Chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PlaylistCountText.Text = Vm.Playlist.Count == 0 ? "" : L.F("{0} items", Vm.Playlist.Count);
        HistoryEmptyText.Visibility = Vm.History.Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        HistoryCountText.Text = Vm.History.Entries.Count == 0 ? "" : L.F("{0} entries", Vm.History.Entries.Count);
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
                case nameof(PlayerViewModel.SecondarySubDelay):
                {
                    double d = SecondaryTarget ? Vm.SecondarySubDelay : Vm.SubDelay;
                    SubDelaySlider.Value = Math.Clamp(d, -10, 10);
                    SubDelayLabel.Text = $"{d:+0.0;-0.0;0.0} s";
                    break;
                }
                case nameof(PlayerViewModel.SubVisible): SubVisibleSwitch.IsOn = Vm.SubVisible; break;
                case nameof(PlayerViewModel.SecondarySubVisible): SecondarySubVisibleSwitch.IsOn = Vm.SecondarySubVisible; break;
                case nameof(PlayerViewModel.SubScale):
                    SubScaleSlider.Value = Vm.SubScale;
                    SubScaleLabel.Text = $"{Vm.SubScale * 100:0}%";
                    break;
                case nameof(PlayerViewModel.SubPos):
                case nameof(PlayerViewModel.SecondarySubPos):
                {
                    long pos = SecondaryTarget ? Vm.SecondarySubPos : Vm.SubPos;
                    SubPosSlider.Value = pos;
                    SubPosLabel.Text = pos.ToString();
                    break;
                }
                case nameof(PlayerViewModel.HwdecCurrent):
                case nameof(PlayerViewModel.FilePath):
                    RefreshHdrInfo();
                    break;
                case nameof(PlayerViewModel.Crop):
                {
                    int ci = Array.IndexOf(CropValues, Vm.Crop);
                    CropButtons.SelectedIndex = ci < 0 ? 0 : ci;
                    break;
                }
                case nameof(PlayerViewModel.VideoFilters):
                    HFlipButton.IsChecked = Vm.VideoFilters.Contains("hflip");
                    VFlipButton.IsChecked = Vm.VideoFilters.Contains("vflip");
                    break;
                case nameof(PlayerViewModel.AudioDevice):
                    AudioDeviceCombo.SelectedItem = Vm.AudioDevices.FirstOrDefault(d => d.Name == Vm.AudioDevice);
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
                     nameof(PlayerViewModel.SubVisible), nameof(PlayerViewModel.SecondarySubVisible),
                 })
            OnVmChanged(this, new PropertyChangedEventArgs(name));
        OnListsChanged(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        _syncing = true;
        HdrCombo.SelectedIndex = (int)Vm.Services.Settings.HdrMode;
        EqSwitch.IsOn = Vm.Services.Settings.EqEnabled;
        for (int i = 0; i < _eqSliders.Length; i++) _eqSliders[i].Value = Vm.Services.Settings.EqGains.Length > i ? Vm.Services.Settings.EqGains[i] : 0;
        OnVmChanged(this, new PropertyChangedEventArgs(nameof(PlayerViewModel.Crop)));
        OnVmChanged(this, new PropertyChangedEventArgs(nameof(PlayerViewModel.VideoFilters)));
        OnVmChanged(this, new PropertyChangedEventArgs(nameof(PlayerViewModel.AudioDevice)));
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
        var d = Vm.Window?.LastDisplay;
        string mode = Vm.Window?.IsHdrPassthrough == true ? L.T("HDR10 passthrough") : "SDR";
        HdrInfo.Text = d is null
            ? L.F("Output: {0}", mode)
            : L.F("Output: {0} / display: {1} {2}-bit, peak {3:0} nits, SDR white {4:0} nits", mode, d.IsHdr ? "HDR" : "SDR", d.BitsPerColor, d.MaxLuminance, d.SdrWhiteNits);
        DecoderInfo.Text = string.IsNullOrEmpty(Vm.HwdecCurrent) || Vm.HwdecCurrent == "no"
            ? L.T("Decoding: software")
            : L.F("Decoding: {0}", Vm.HwdecCurrent);
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

    private void CropButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CropButtons.SelectedIndex < 0) return;
        string v = CropValues[CropButtons.SelectedIndex];
        if (v == Vm.Crop) return;
        Vm.SetCrop(v);
    }

    private void HFlip_Click(object sender, RoutedEventArgs e) => Vm.ToggleFlip(horizontal: true);
    private void VFlip_Click(object sender, RoutedEventArgs e) => Vm.ToggleFlip(horizontal: false);
    private void ResetZoom_Click(object sender, RoutedEventArgs e) => Vm.ResetZoom();

    // ---- equalizer -----------------------------------------------------------------------

    private void BuildEq()
    {
        for (int i = 0; i < 10; i++)
        {
            EqGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var s = new Slider
            {
                Orientation = Orientation.Vertical, Minimum = -12, Maximum = 12, StepFrequency = 0.5, Value = 0,
                IsThumbToolTipEnabled = true, HorizontalAlignment = HorizontalAlignment.Center, Height = 120, Tag = i,
            };
            s.ValueChanged += EqBand_ValueChanged;
            var label = new TextBlock
            {
                Text = PlayerViewModel.EqBands[i] >= 1000 ? $"{PlayerViewModel.EqBands[i] / 1000}k" : PlayerViewModel.EqBands[i].ToString(),
                FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["OverlaySubtleTextBrush"],
            };
            var cell = new Grid();
            cell.Children.Add(s);
            cell.Children.Add(label);
            Grid.SetColumn(cell, i);
            EqGrid.Children.Add(cell);
            _eqSliders[i] = s;
        }
    }

    private void EqBand_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        PushEq();
    }

    private void EqSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        PushEq();
    }

    private void EqFlat_Click(object sender, RoutedEventArgs e)
    {
        _syncing = true;
        foreach (var s in _eqSliders) s.Value = 0;
        _syncing = false;
        PushEq();
    }

    private void PushEq()
    {
        var gains = _eqSliders.Select(s => s.Value).ToArray();
        Vm.Services.Settings.EqGains = gains;
        Vm.Services.Settings.EqEnabled = EqSwitch.IsOn;
        Vm.Services.Settings.Save();
        Vm.ApplyEq(gains, EqSwitch.IsOn);
    }

    private void AudioDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || AudioDeviceCombo.SelectedItem is not AudioDeviceInfo d || d.Name == Vm.AudioDevice) return;
        Vm.SetAudioDevice(d.Name);
    }

    private void HdrCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || HdrCombo.SelectedIndex < 0) return;
        Vm.Services.Settings.HdrMode = (HdrMode)HdrCombo.SelectedIndex;
        Vm.Services.Settings.Save();
        foreach (var w in Vm.Services.Windows.All) w.ApplyHdr();
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

    /// <summary>Delay / position sliders edit the secondary selector (IINA's Primary / Secondary switch).</summary>
    private bool SecondaryTarget => SubTargetButtons.SelectedIndex == 1;

    private void SubTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.ChooseSub(t, secondary: false);
    }

    private void SecondarySubTrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackInfo t) Vm.ChooseSub(t, secondary: true);
    }

    private void SubVisibleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_syncing && SubVisibleSwitch.IsOn != Vm.SubVisible) Vm.SetSubVisible(SubVisibleSwitch.IsOn);
    }

    private void SecondarySubVisibleSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_syncing && SecondarySubVisibleSwitch.IsOn != Vm.SecondarySubVisible) Vm.SetSubVisible(SecondarySubVisibleSwitch.IsOn, secondary: true);
    }

    private void SubTargetButtons_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Vm is null) return;
        OnVmChanged(this, new PropertyChangedEventArgs(nameof(PlayerViewModel.SubDelay)));
        OnVmChanged(this, new PropertyChangedEventArgs(nameof(PlayerViewModel.SubPos)));
    }

    private async void AddSub_Click(object sender, RoutedEventArgs e)
    {
        var file = await PickFileAsync([".srt", ".ass", ".ssa", ".sub", ".vtt", ".sup", ".idx", ".txt"]);
        if (file is not null) Vm.AddSubtitle(file);
    }

    private async void SearchOnlineSub_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OnlineSubtitlesDialog(Vm, Vm.FilePath, Vm.MediaTitle) { XamlRoot = XamlRoot };
        await dlg.ShowAsync();
    }

    private async void SubStyle_Click(object sender, RoutedEventArgs e)
    {
        if (Vm.Window is { } w) await w.ShowPreferencesAsync("subtitles");
    }

    private void SubDelaySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        if (SecondaryTarget) Vm.SetSecondarySubDelay(Math.Round(e.NewValue, 2)); else Vm.SetSubDelay(Math.Round(e.NewValue, 2));
    }

    private void ResetSubDelay_Click(object sender, RoutedEventArgs e)
    {
        if (SecondaryTarget) Vm.SetSecondarySubDelay(0); else Vm.SetSubDelay(0);
    }

    private void SubScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        Vm.SetSubScale(Math.Round(e.NewValue, 2));
    }

    private void SubPosSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncing) return;
        if (SecondaryTarget) Vm.SetSecondarySubPos((long)Math.Round(e.NewValue)); else Vm.SetSubPos((long)Math.Round(e.NewValue));
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
        var files = await PickFilesAsync(Vm.Window, FileAssociation.AllExtensions);
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

    private async Task<string?> PickFileAsync(IEnumerable<string> extensions)
    {
        var files = await PickFilesAsync(Vm.Window, extensions, multiple: false);
        return files.Count > 0 ? files[0] : null;
    }

    public static async Task<List<string>> PickFilesAsync(MainWindow? owner, IEnumerable<string> extensions, bool multiple = true)
    {
        var result = new List<string>();
        if (owner is null) return result;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.VideosLibrary };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(owner));
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
