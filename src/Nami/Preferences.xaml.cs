using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nami.Services;
using Windows.System;

namespace Nami;

public sealed partial class Preferences : ContentDialog
{
    private static string MpvConfPath => Path.Combine(AppSettings.MpvConfigDirectory, "mpv.conf");
    private static string InputConfPath => Path.Combine(AppSettings.MpvConfigDirectory, "input.conf");

    public Preferences()
    {
        InitializeComponent();
        var s = App.Settings;
        ResizeSwitch.IsOn = s.ResizeWindowToVideo;
        RememberVolumeSwitch.IsOn = s.RememberVolume;
        ResumeSwitch.IsOn = s.ResumePlayback;
        AutoLoadFolderSwitch.IsOn = s.AutoLoadFolder;
        HistorySwitch.IsOn = s.KeepHistory;
        MpvConfBox.Text = ReadOrEmpty(MpvConfPath);
        InputConfBox.Text = ReadOrEmpty(InputConfPath);
        UpdateAssocStatus();
    }

    private static string ReadOrEmpty(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { return ""; }
    }

    private void UpdateAssocStatus()
    {
        bool reg = FileAssociation.IsRegistered;
        AssocStatus.Text = reg ? "状態: 登録済み" : "状態: 未登録";
        RegisterButton.IsEnabled = !reg;
        UnregisterButton.IsEnabled = reg;
    }

    private void Register_Click(object sender, RoutedEventArgs e)
    {
        try { FileAssociation.Register(); }
        catch (Exception ex) { AssocStatus.Text = "登録に失敗しました: " + ex.Message; return; }
        UpdateAssocStatus();
    }

    private void Unregister_Click(object sender, RoutedEventArgs e)
    {
        try { FileAssociation.Unregister(); }
        catch (Exception ex) { AssocStatus.Text = "解除に失敗しました: " + ex.Message; return; }
        UpdateAssocStatus();
    }

    private async void OpenConfigFolder_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(AppSettings.MpvConfigDirectory);
        await Launcher.LaunchFolderPathAsync(AppSettings.MpvConfigDirectory);
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var s = App.Settings;
        s.ResizeWindowToVideo = ResizeSwitch.IsOn;
        s.RememberVolume = RememberVolumeSwitch.IsOn;
        s.ResumePlayback = ResumeSwitch.IsOn;
        s.AutoLoadFolder = AutoLoadFolderSwitch.IsOn;
        s.KeepHistory = HistorySwitch.IsOn;
        if (App.Vm.Player is { } pl)
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
        if (App.Vm.Player is { } p)
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
