using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nami.Services;
using Windows.System;

namespace Nami;

public sealed partial class OnlineSubtitlesDialog : ContentDialog
{
    private readonly OpenSubtitles _client = new();
    private readonly string? _filePath;
    private string _apiKey = "", _user = "", _password = "";
    private CancellationTokenSource? _cts;

    public OnlineSubtitlesDialog(string? filePath, string? title)
    {
        InitializeComponent();
        _filePath = filePath;
        QueryBox.Text = GuessQuery(filePath, title);
        LanguagesBox.Text = App.Settings.SubtitleLanguages;
        LoadCredentials();
        Closing += (_, _) => _cts?.Cancel();
    }

    private static string GuessQuery(string? path, string? title)
    {
        string name = path is not null && !path.Contains("://") ? Path.GetFileNameWithoutExtension(path) : (title ?? "");
        // Strip the usual release-name noise so the text search has a chance.
        name = System.Text.RegularExpressions.Regex.Replace(name, @"[._\[\]()]", " ");
        name = System.Text.RegularExpressions.Regex.Replace(name, @"\b(1080p|720p|2160p|4k|x264|x265|h264|h265|hevc|aac|bluray|webrip|web-dl|hdrip|dvdrip|remux)\b.*$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return name.Trim();
    }

    private void LoadCredentials()
    {
        (_apiKey, _user, _password) = OpenSubtitles.LoadCredentials();
        bool ok = !string.IsNullOrEmpty(_apiKey);
        CredentialsPanel.Visibility = ok ? Visibility.Collapsed : Visibility.Visible;
        ApiKeyBox.Text = _apiKey;
        UserBox.Text = _user;
        SearchButton.IsEnabled = ok;
        if (!ok) Status.Text = L.T("API キーを保存すると検索できます。ダウンロードにはアカウントのログインも必要です。");
    }

    private void SaveCredentials_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            OpenSubtitles.SaveCredentials(ApiKeyBox.Text, UserBox.Text, PasswordBox.Password);
            LoadCredentials();
            Status.Text = L.T("保存しました。");
        }
        catch (Exception ex) { Status.Text = L.T("保存に失敗しました: ") + ex.Message; }
    }

    private void QueryBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { Search_Click(sender, e); e.Handled = true; }
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        Busy.Visibility = Visibility.Visible;
        Status.Text = "";
        Results.ItemsSource = null;
        App.Settings.SubtitleLanguages = LanguagesBox.Text.Trim();
        App.Settings.Save();
        try
        {
            var list = await _client.SearchAsync(_apiKey, _filePath, QueryBox.Text.Trim(), LanguagesBox.Text.Trim(), _cts.Token);
            Results.ItemsSource = list;
            Status.Text = list.Count == 0 ? L.T("見つかりませんでした。") : L.F("{0} 件（ハッシュ一致は上位に表示）", list.Count);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally { Busy.Visibility = Visibility.Collapsed; }
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => IsPrimaryButtonEnabled = Results.SelectedItem is not null;

    private async void OnDownload(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (Results.SelectedItem is not SubtitleSearchResult r) return;
        var deferral = args.GetDeferral();
        args.Cancel = true; // keep the dialog open until we know it worked
        Busy.Visibility = Visibility.Visible;
        try
        {
            string dir = _filePath is not null && !_filePath.Contains("://") && Path.GetDirectoryName(_filePath) is { } d && IsWritable(d)
                ? d
                : Path.Combine(AppSettings.Directory, "subtitles");
            string baseName = _filePath is not null && !_filePath.Contains("://")
                ? Path.GetFileNameWithoutExtension(_filePath)
                : "subtitle";
            string path = await _client.DownloadAsync(_apiKey, _user, _password, r, dir, baseName, _cts?.Token ?? CancellationToken.None);
            App.Vm.AddSubtitle(path);
            Status.Text = L.T("追加しました: ") + path;
            Hide();
        }
        catch (Exception ex) { Status.Text = ex.Message; }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            deferral.Complete();
        }
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            string probe = Path.Combine(dir, ".nami-write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
