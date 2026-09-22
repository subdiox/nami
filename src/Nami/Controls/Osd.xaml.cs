using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nami.Player;
using Nami.Services;

namespace Nami.Controls;

/// <summary>
/// IINA-style on-screen display: a translucent panel in a corner of the video that shows the
/// last notification (icon, text, optional detail line and bar) and fades out after a delay.
/// </summary>
public sealed partial class Osd : UserControl
{
    private readonly DispatcherQueueTimer _hideTimer;
    private Storyboard? _fade;
    private OsdSettings _settings = new();

    private string? _shotPath;

    /// <summary>True while the screenshot preview (with its buttons) is on screen.</summary>
    public bool IsInteractive => _shotPath is not null && Visibility == Visibility.Visible;

    /// <summary>Raised when the OSD becomes / stops being clickable (the caption regions must follow).</summary>
    public event Action? InteractiveChanged;

    public Osd()
    {
        InitializeComponent();
        ShotDelete.Content = L.T("Delete");
        ShotReveal.Content = L.T("Show in Explorer");
        ShotEdit.Content = L.T("Edit");
        _hideTimer = DispatcherQueue.CreateTimer();
        _hideTimer.IsRepeating = false;
        _hideTimer.Tick += (_, _) => Hide();
    }

    /// <summary>Apply position / scale / duration from the settings.</summary>
    public void Configure(OsdSettings settings)
    {
        _settings = settings;
        HorizontalAlignment = settings.Position is OsdPosition.TopRight or OsdPosition.BottomRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        VerticalAlignment = settings.Position is OsdPosition.BottomLeft or OsdPosition.BottomRight ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        // Leave room for the title bar at the top and the OSC at the bottom.
        Margin = VerticalAlignment == VerticalAlignment.Top ? new Thickness(24, 56, 24, 24) : new Thickness(24, 24, 24, 110);
        double scale = settings.Scale switch { OsdScale.Small => 0.85, OsdScale.Large => 1.25, _ => 1.0 };
        Panel.RenderTransform = new Microsoft.UI.Xaml.Media.ScaleTransform { ScaleX = scale, ScaleY = scale };
        Panel.RenderTransformOrigin = new Windows.Foundation.Point(HorizontalAlignment == HorizontalAlignment.Left ? 0 : 1, VerticalAlignment == VerticalAlignment.Top ? 0 : 1);
    }

    /// <summary>Show a message, replacing whatever is on screen and restarting the timer.</summary>
    public void Show(OsdMessage m)
    {
        if (!_settings.Enabled) return;
        Icon.Visibility = Visibility.Collapsed;   // IINA's OSD is text (and a bar) only
        Text.Text = m.Text;
        Detail.Text = m.Detail ?? "";
        Detail.Visibility = string.IsNullOrEmpty(m.Detail) ? Visibility.Collapsed : Visibility.Visible;
        if (m.Progress is double p)
        {
            Bar.Value = Math.Clamp(p, 0, 1);
            Bar.Visibility = Visibility.Visible;
        }
        else Bar.Visibility = Visibility.Collapsed;

        bool wasInteractive = IsInteractive;
        _shotPath = m.ImagePath;
        if (_shotPath is not null)
        {
            try
            {
                var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = 480 };
                bmp.UriSource = new Uri(_shotPath);
                ShotImage.Source = bmp;
            }
            catch { ShotImage.Source = null; }
            ShotPanel.Visibility = Visibility.Visible;
        }
        else
        {
            ShotPanel.Visibility = Visibility.Collapsed;
            ShotImage.Source = null;
        }
        IsHitTestVisible = _shotPath is not null;

        // Updating a visible message must not restart the fade-in: stopping the storyboard resets
        // Opacity to its base value (0) for a frame, which reads as flicker while scrubbing.
        if (Visibility == Visibility.Visible && Opacity >= 0.99)
        {
            _fade?.Stop();
            _fade = null;
            Opacity = 1;
        }
        else
        {
            Visibility = Visibility.Visible;
            Fade(1, 90);
        }
        _hideTimer.Stop();
        _hideTimer.Interval = TimeSpan.FromSeconds(Math.Max(0.3, m.Seconds ?? _settings.DurationSeconds));
        _hideTimer.Start();
        if (wasInteractive != IsInteractive) InteractiveChanged?.Invoke();
    }

    public void Hide()
    {
        _hideTimer.Stop();
        bool wasInteractive = IsInteractive;
        _shotPath = null;
        IsHitTestVisible = false;
        Fade(0, 260, collapseWhenDone: true);
        if (wasInteractive) InteractiveChanged?.Invoke();
    }

    private void ShotDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_shotPath is { } p) { try { File.Delete(p); } catch (Exception ex) { App.Log("screenshot delete: " + ex.Message); } }
        Hide();
    }

    private void ShotReveal_Click(object sender, RoutedEventArgs e)
    {
        if (_shotPath is { } p)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = false }); }
            catch (Exception ex) { App.Log("screenshot reveal: " + ex.Message); }
        }
        Hide();
    }

    private async void ShotEdit_Click(object sender, RoutedEventArgs e)
    {
        if (_shotPath is { } p)
        {
            try { await Windows.System.Launcher.LaunchFileAsync(await Windows.Storage.StorageFile.GetFileFromPathAsync(p)); }
            catch (Exception ex) { App.Log("screenshot edit: " + ex.Message); }
        }
        Hide();
    }

    private void Fade(double to, int ms, bool collapseWhenDone = false)
    {
        _fade?.Stop();
        var anim = new DoubleAnimation
        {
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(ms)),
            EasingFunction = new CubicEase { EasingMode = to > 0 ? EasingMode.EaseOut : EasingMode.EaseIn },
        };
        Storyboard.SetTarget(anim, this);
        Storyboard.SetTargetProperty(anim, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(anim);
        if (collapseWhenDone)
            sb.Completed += (_, _) => { if (Opacity < 0.01) Visibility = Visibility.Collapsed; };
        _fade = sb;
        sb.Begin();
    }
}
