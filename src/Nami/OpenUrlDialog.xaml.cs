using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nami.Services;
using Windows.ApplicationModel.DataTransfer;

namespace Nami;

public sealed partial class OpenUrlDialog : ContentDialog
{
    public string Url => UrlBox.Text.Trim();

    public OpenUrlDialog()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            // IINA pre-fills the field from the clipboard when it holds a URL.
            try
            {
                var data = Clipboard.GetContent();
                if (data.Contains(StandardDataFormats.Text))
                {
                    string text = (await data.GetTextAsync()).Trim();
                    if (Uri.TryCreate(text, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" or "rtmp" or "rtsp")
                    {
                        UrlBox.Text = text;
                        UrlBox.SelectAll();
                    }
                }
            }
            catch { }
            UrlBox.Focus(FocusState.Programmatic);
            UpdateYtDlpHint();
        };
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateYtDlpHint();

    private void UpdateYtDlpHint()
    {
        bool needs = YtDlp.LooksLikeStreamingSite(Url) && !YtDlp.IsInstalled;
        YtDlpPanel.Visibility = needs ? Visibility.Visible : Visibility.Collapsed;
        if (needs) YtDlpText.Text = L.T("Playing this kind of URL requires yt-dlp. Download it and mpv will use it automatically.");
    }

    private async void YtDlpButton_Click(object sender, RoutedEventArgs e)
    {
        YtDlpButton.IsEnabled = false;
        YtDlpProgress.Visibility = Visibility.Visible;
        try
        {
            await YtDlp.InstallOrUpdateAsync(new Progress<double>(v => YtDlpProgress.Value = v * 100), CancellationToken.None);
            YtDlpText.Text = L.T("yt-dlp installed: ") + await YtDlp.GetVersionAsync();
            YtDlpButton.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            YtDlpText.Text = L.T("Download failed: ") + ex.Message;
            YtDlpButton.IsEnabled = true;
        }
        finally { YtDlpProgress.Visibility = Visibility.Collapsed; }
    }
}
