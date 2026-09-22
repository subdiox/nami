using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Nami.Services;
using Windows.System;

namespace Nami;

public sealed partial class Preferences : ContentDialog
{
    private static string MpvConfPath => Path.Combine(AppSettings.MpvConfigDirectory, "mpv.conf");
    private static string InputConfPath => Path.Combine(AppSettings.MpvConfigDirectory, "input.conf");

    private readonly Player.PlayerViewModel _vm;

    public Preferences(Player.PlayerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        OscLayoutCombo.ItemsSource = new List<string> {L.T("Floating (default)"), L.T("Bottom bar"), L.T("Top bar")};
        SubBorderStyleCombo.ItemsSource = new List<string> {L.T("Outline and shadow"), L.T("Opaque box"), L.T("Background box")};
        SubAssOverrideCombo.ItemsSource = new List<string> {L.T("Use the styles from the subtitle file"), L.T("Override with these settings (yes)"), L.T("Match size only (scale)"), L.T("Force override (force)"), L.T("Strip styling (strip)")};
        var s = _vm.Services.Settings;
        LanguageCombo.ItemsSource = L.Available.Select(a => a.name).ToList();
        int li = Array.FindIndex(L.Available, a => a.code == s.Language);
        LanguageCombo.SelectedIndex = li < 0 ? 0 : li;
        ResizeSwitch.IsOn = s.ResizeWindowToVideo;
        RememberVolumeSwitch.IsOn = s.RememberVolume;
        NewWindowSwitch.IsOn = s.OpenInNewWindow;
        ResumeSwitch.IsOn = s.ResumePlayback;
        AutoLoadFolderSwitch.IsOn = s.AutoLoadFolder;
        HistorySwitch.IsOn = s.KeepHistory;
        ThumbnailSwitch.IsOn = s.SeekThumbnails;
        AutoMusicSwitch.IsOn = s.AutoMusicMode;
        OscLayoutCombo.SelectedIndex = (int)s.OscLayout;
        MpvConfBox.Text = ReadOrEmpty(MpvConfPath);
        InputConfBox.Text = ReadOrEmpty(InputConfPath);
        UpdateAssocStatus();
        LoadSubtitleStyle(s.Subtitles);
        SubLanguagesBox.Text = s.SubtitleLanguages;
        YtdlFormatCombo.ItemsSource = YtDlp.Formats.Select(f => f.label).ToList();
        int fi = Array.FindIndex(YtDlp.Formats, f => f.value == s.YtdlFormat);
        YtdlFormatCombo.SelectedIndex = fi < 0 ? 3 : fi;
        _ = RefreshYtDlpStatusAsync();
        ScreenshotDirBox.Text = s.ScreenshotDirectory;
        ScreenshotFormatCombo.SelectedItem = s.ScreenshotFormat;
        if (ScreenshotFormatCombo.SelectedIndex < 0) ScreenshotFormatCombo.SelectedIndex = 0;
    }

    private async void BrowseScreenshotDir_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Window is null) return;
        var picker = new Windows.Storage.Pickers.FolderPicker { SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(_vm.Window));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) ScreenshotDirBox.Text = folder.Path;
    }

    private async Task RefreshYtDlpStatusAsync()
    {
        string? v = await YtDlp.GetVersionAsync();
        YtDlpStatus.Text = v is null ? L.T("yt-dlp is not installed (needed for YouTube and similar sites)") : L.F("yt-dlp {0} ({1})", v, YtDlp.ExePath);
    }

    private async void YtDlpInstall_Click(object sender, RoutedEventArgs e)
    {
        YtDlpInstallButton.IsEnabled = false;
        YtDlpProgress.Visibility = Visibility.Visible;
        try
        {
            await YtDlp.InstallOrUpdateAsync(new Progress<double>(v => YtDlpProgress.Value = v * 100), CancellationToken.None);
            await RefreshYtDlpStatusAsync();
        }
        catch (Exception ex) { YtDlpStatus.Text = L.T("Download failed: ") + ex.Message; }
        finally
        {
            YtDlpProgress.Visibility = Visibility.Collapsed;
            YtDlpInstallButton.IsEnabled = true;
        }
    }

    // ---- subtitle style ----------------------------------------------------------------

    private static readonly string[] BorderStyles = ["outline-and-shadow", "opaque-box", "background-box"];
    private static readonly string[] AssOverrides = ["no", "yes", "scale", "force", "strip"];
    private string _subColor = "#FFFFFFFF", _subOutline = "#FF000000", _subBack = "#00000000";

    private void LoadSubtitleStyle(SubtitleStyle st)
    {
        var fonts = SubtitleStyle.SystemFonts();
        fonts.Insert(0, L.T("(default)"));
        SubFontCombo.ItemsSource = fonts;
        SubFontCombo.Text = string.IsNullOrEmpty(st.Font) ? L.T("(default)") : st.Font;
        SubSizeSlider.Value = st.Size;
        SubBoldCheck.IsChecked = st.Bold;
        SubItalicCheck.IsChecked = st.Italic;
        SubBorderStyleCombo.SelectedIndex = Math.Max(0, Array.IndexOf(BorderStyles, st.BorderStyle));
        SubOutlineSlider.Value = st.OutlineSize;
        SubShadowSlider.Value = st.ShadowOffset;
        SubCodepageCombo.ItemsSource = SubtitleStyle.Codepages;
        SubCodepageCombo.SelectedIndex = Math.Max(0, Array.IndexOf(SubtitleStyle.Codepages, st.Codepage));
        SubAssOverrideCombo.SelectedIndex = Math.Max(0, Array.IndexOf(AssOverrides, st.AssOverride));
        _subColor = st.Color; _subOutline = st.OutlineColor; _subBack = st.BackColor;
        PaintColorButtons();
    }

    private void PaintColorButtons()
    {
        SubColorButton.Background = new SolidColorBrush(ParseColor(_subColor));
        SubOutlineColorButton.Background = new SolidColorBrush(ParseColor(_subOutline));
        SubBackColorButton.Background = new SolidColorBrush(ParseColor(_subBack));
    }

    private static Windows.UI.Color ParseColor(string s)
    {
        try
        {
            s = s.TrimStart('#');
            if (s.Length == 6) s = "FF" + s;
            uint v = Convert.ToUInt32(s, 16);
            return Windows.UI.Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
        }
        catch { return Colors.White; }
    }

    private static string FormatColor(Windows.UI.Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private void SubColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string which) return;
        string current = which switch { "text" => _subColor, "outline" => _subOutline, _ => _subBack };
        var picker = new ColorPicker { IsAlphaEnabled = true, IsMoreButtonVisible = true, ColorSpectrumShape = ColorSpectrumShape.Ring, Color = ParseColor(current), Width = 300 };
        picker.ColorChanged += (_, args) =>
        {
            string v = FormatColor(args.NewColor);
            switch (which) { case "text": _subColor = v; break; case "outline": _subOutline = v; break; default: _subBack = v; break; }
            PaintColorButtons();
        };
        var fly = new Flyout { Content = picker };
        fly.ShowAt(b);
    }

    private void SubStyleReset_Click(object sender, RoutedEventArgs e) => LoadSubtitleStyle(new SubtitleStyle());

    private SubtitleStyle CollectSubtitleStyle()
    {
        var old = _vm.Services.Settings.Subtitles;
        string font = SubFontCombo.Text?.Trim() ?? "";
        return new SubtitleStyle
        {
            Font = font == L.T("(default)") ? "" : font,
            Size = Math.Round(SubSizeSlider.Value),
            Bold = SubBoldCheck.IsChecked == true,
            Italic = SubItalicCheck.IsChecked == true,
            BorderStyle = BorderStyles[Math.Max(0, SubBorderStyleCombo.SelectedIndex)],
            OutlineSize = SubOutlineSlider.Value,
            ShadowOffset = SubShadowSlider.Value,
            Codepage = SubtitleStyle.Codepages[Math.Max(0, SubCodepageCombo.SelectedIndex)],
            AssOverride = AssOverrides[Math.Max(0, SubAssOverrideCombo.SelectedIndex)],
            Color = _subColor,
            OutlineColor = _subOutline,
            BackColor = _subBack,
            Position = old.Position,
            Scale = old.Scale,
        };
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private void UpdateAssocStatus()
    {
        bool reg = FileAssociation.IsRegistered;
        AssocStatus.Text = reg ? L.T("Status: registered") : L.T("Status: not registered");
        RegisterButton.IsEnabled = !reg;
        UnregisterButton.IsEnabled = reg;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try { FileAssociation.Register(); }
        catch (Exception ex) { AssocStatus.Text = L.T("Registration failed: ") + ex.Message; return; }
        UpdateAssocStatus();
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try { FileAssociation.Unregister(); }
        catch (Exception ex) { AssocStatus.Text = L.T("Unregistering failed: ") + ex.Message; return; }
        UpdateAssocStatus();
    }

    private async void KeyBindings_Click(object sender, RoutedEventArgs e)
    {
        // ContentDialogs cannot stack; close this one, show the editor, then reopen preferences.
        Hide();
        var dlg = new KeyBindingsDialog(_vm) { XamlRoot = XamlRoot };
        await dlg.ShowAsync();
        if (_vm.Window is { } w) _ = w.ShowPreferencesAsync();
    }

    private async void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.MpvConfigDirectory);
        await Launcher.LaunchFolderPathAsync(AppSettings.MpvConfigDirectory);
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var s = _vm.Services.Settings;
        s.ResizeWindowToVideo = ResizeSwitch.IsOn;
        if (LanguageCombo.SelectedIndex >= 0) s.Language = L.Available[LanguageCombo.SelectedIndex].code;
        s.RememberVolume = RememberVolumeSwitch.IsOn;
        s.OpenInNewWindow = NewWindowSwitch.IsOn;
        s.ResumePlayback = ResumeSwitch.IsOn;
        s.AutoLoadFolder = AutoLoadFolderSwitch.IsOn;
        s.KeepHistory = HistorySwitch.IsOn;
        s.SeekThumbnails = ThumbnailSwitch.IsOn;
        s.AutoMusicMode = AutoMusicSwitch.IsOn;
        s.ScreenshotDirectory = ScreenshotDirBox.Text.Trim();
        s.ScreenshotFormat = ScreenshotFormatCombo.SelectedItem as string ?? "png";
        foreach (var shp in _vm.Services.Windows.Players)
        {
            try
            {
                shp.SetProperty("screenshot-directory", string.IsNullOrEmpty(s.ScreenshotDirectory)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : s.ScreenshotDirectory);
                shp.SetProperty("screenshot-format", s.ScreenshotFormat);
            }
            catch (Mpv.MpvException) { }
        }
        s.OscLayout = (OscLayout)Math.Max(0, OscLayoutCombo.SelectedIndex);
        foreach (var w in _vm.Services.Windows.All) w.Page.ApplyOscLayout(s.OscLayout);
        if (YtdlFormatCombo.SelectedIndex >= 0)
        {
            s.YtdlFormat = YtDlp.Formats[YtdlFormatCombo.SelectedIndex].value;
            foreach (var yp in _vm.Services.Windows.Players) { try { yp.SetProperty("ytdl-format", s.YtdlFormat); } catch (Mpv.MpvException) { } }
        }
        s.Subtitles = CollectSubtitleStyle();
        s.SubtitleLanguages = SubLanguagesBox.Text.Trim();
        foreach (var sp in _vm.Services.Windows.Players) s.Subtitles.Apply(sp);
        foreach (var pl in _vm.Services.Windows.Players)
        {
            try
            {
                pl.SetProperty("save-position-on-quit", s.ResumePlayback);
                pl.SetProperty("resume-playback", s.ResumePlayback);
            }
            catch (Mpv.MpvException) { }
        }
        s.Save();

        Directory.CreateDirectory(AppSettings.MpvConfigDirectory);
        File.WriteAllText(MpvConfPath, MpvConfBox.Text);
        File.WriteAllText(InputConfPath, InputConfBox.Text);

        // Best-effort live apply of mpv.conf: "name=value" lines become property sets.
        foreach (var p in _vm.Services.Windows.Players)
        {
            foreach (var raw in MpvConfBox.Text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith('[')) continue;
                int eq = line.IndexOf('=');
                string name = eq < 0 ? line : line[..eq].Trim();
                string value = eq < 0 ? "yes" : line[(eq + 1)..].Trim().Trim('"');
                if (name.StartsWith("no-", StringComparison.Ordinal)) { name = name[3..]; value = "no"; }
                try { p.SetProperty(name, value); } catch (Mpv.MpvException) { }
            }
        }
    }
}
