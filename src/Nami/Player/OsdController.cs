using System.ComponentModel;
using System.Globalization;
using Nami.Services;

namespace Nami.Player;

/// <summary>
/// Decides which player state changes deserve an on-screen message, the way IINA does it
/// in its command handlers. Because keys are forwarded to mpv, changes arrive as property
/// updates, so this watches the view model and suppresses the noise around file loads.
/// </summary>
public sealed class OsdController : IDisposable
{
    private readonly PlayerViewModel _vm;
    private readonly Action<OsdMessage> _show;
    private DateTime _armedAt = DateTime.MaxValue;
    private bool _seekPending;
    private string? _lastFile;

    // previous values, to tell real changes from re-notifications
    private bool _paused = true;
    private double _volume = -1;
    private bool _muted;
    private double _speed = 1;
    private string _sid = "", _aid = "", _vid = "";
    private double _subDelay, _audioDelay, _subScale = 1;
    private long _subPos = 100, _rotate;
    private string _aspect = "no", _crop = "";
    private bool _deinterlace, _shuffle, _loopFile, _loopPlaylist;
    private long _brightness, _contrast, _saturation, _gamma, _hue, _chapter = -1;
    private double _abA = double.NaN, _abB = double.NaN;
    private double _zoom;

    public OsdController(PlayerViewModel vm, Action<OsdMessage> show)
    {
        _vm = vm;
        _show = show;
        vm.PropertyChanged += OnChanged;
        vm.FileLoaded += OnFileLoaded;
        vm.PlaybackRestart += OnPlaybackRestart;
        vm.SeekStarted += () => _seekPending = true;
        vm.ClientMessage += OnClientMessage;
        vm.ScreenshotSaved += path => Show(new OsdMessage(OsdMessage.IconScreenshot, L.T("Screenshot Captured"), Seconds: 6, ImagePath: path));
        vm.OsdRequested += Show;
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnChanged;
        _vm.FileLoaded -= OnFileLoaded;
        _vm.PlaybackRestart -= OnPlaybackRestart;
    }

    private bool Armed => DateTime.UtcNow >= _armedAt && !_vm.Idle;

    private void Show(OsdMessage m) => _show(m);

    private void OnFileLoaded()
    {
        // Snapshot everything so the initial property burst is not reported as user changes.
        _paused = _vm.Paused; _volume = _vm.Volume; _muted = _vm.Muted; _speed = _vm.Speed;
        _sid = _vm.Sid; _aid = _vm.Aid; _vid = _vm.Vid;
        _subDelay = _vm.SubDelay; _audioDelay = _vm.AudioDelay; _subScale = _vm.SubScale; _subPos = _vm.SubPos;
        _rotate = _vm.Rotate; _aspect = _vm.Aspect; _crop = _vm.Crop; _deinterlace = _vm.Deinterlace;
        _shuffle = _vm.Shuffle; _loopFile = _vm.LoopFile; _loopPlaylist = _vm.LoopPlaylist;
        _brightness = _vm.Brightness; _contrast = _vm.Contrast; _saturation = _vm.Saturation; _gamma = _vm.Gamma; _hue = _vm.Hue;
        _chapter = _vm.Chapter; _abA = _vm.AbLoopA; _abB = _vm.AbLoopB; _zoom = _vm.VideoZoom;
        _armedAt = DateTime.UtcNow.AddMilliseconds(800);
        _seekPending = false;
        _lastFile = _vm.FilePath;
    }

    /// <summary>IINA-style one-liner: "Label: value".</summary>
    private static string Line(string label, string value) => $"{label}: {value}";

    private void OnPlaybackRestart()
    {
        if (!_seekPending) return;
        _seekPending = false;
        if (!Armed) return;
        double d = _vm.Duration;
        Show(new OsdMessage(OsdMessage.IconForward,
            d > 0 ? $"{Fmt.Time(_vm.TimePos)} / {Fmt.Time(d)}" : Fmt.Time(_vm.TimePos), null,
            d > 0 ? _vm.TimePos / d : null));
    }

    private void OnClientMessage(string[] args)
    {
        // script-message osd "text" ["detail"]   — bridge for input.conf / Lua scripts
        if (args.Length >= 2 && args[0] == "osd")
            Show(new OsdMessage(OsdMessage.IconInfo, args[1], args.Length >= 3 ? args[2] : null));
    }

    private static string Signed(double v) => v.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture);

    /// <summary>IINA: "Subtitle Delay: 0.50s later" / "… earlier" / "… No delay".</summary>
    private static string Delay(string label, double seconds)
    {
        if (Math.Abs(seconds) < 0.0005) return Line(label, L.T("No delay"));
        string v = Math.Abs(seconds).ToString("0.00", CultureInfo.InvariantCulture) + "s";
        return Line(label, seconds > 0 ? L.F("{0} later", v) : L.F("{0} earlier", v));
    }

    private string TrackName(IEnumerable<TrackInfo> tracks, string id)
    {
        if (id is "no" or "") return L.T("Off");
        var t = tracks.FirstOrDefault(x => x.Id.ToString() == id);
        return t is null ? id : t.Display;
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Paused):
                if (_vm.Paused != _paused)
                {
                    _paused = _vm.Paused;
                    if (Armed) Show(new OsdMessage(_paused ? OsdMessage.IconPause : OsdMessage.IconPlay, L.T(_paused ? "Pause" : "Resume")));
                }
                break;

            case nameof(PlayerViewModel.Volume):
            case nameof(PlayerViewModel.Muted):
                if (Math.Abs(_vm.Volume - _volume) > 0.01 || _vm.Muted != _muted)
                {
                    bool muteToggled = _vm.Muted != _muted;
                    _volume = _vm.Volume; _muted = _vm.Muted;
                    if (!Armed) break;
                    double v = _vm.Volume;
                    string icon = _vm.Muted || v <= 0 ? OsdMessage.IconMute : v < 34 ? OsdMessage.IconVolumeLow : v < 67 ? OsdMessage.IconVolumeMid : OsdMessage.IconVolume;
                    if (muteToggled)
                        Show(new OsdMessage(icon, L.T(_vm.Muted ? "Mute" : "Unmute"), null, _vm.Muted ? 0 : Math.Min(1, v / 100)));
                    else
                        Show(new OsdMessage(icon, L.F("Volume: {0}", (int)Math.Round(v)), null, Math.Min(1, v / 100)));
                }
                break;

            case nameof(PlayerViewModel.Speed):
                if (Math.Abs(_vm.Speed - _speed) > 0.0001)
                {
                    _speed = _vm.Speed;
                    if (Armed) Show(new OsdMessage(OsdMessage.IconSpeed, L.F("Speed: {0}x", _speed.ToString("0.00", CultureInfo.InvariantCulture))));
                }
                break;

            case nameof(PlayerViewModel.Sid):
                if (_vm.Sid != _sid) { _sid = _vm.Sid; if (Armed) Show(new OsdMessage(OsdMessage.IconSubtitle, Line(L.T("Subtitle"), TrackName(_vm.SubTracks, _sid)))); }
                break;
            case nameof(PlayerViewModel.Aid):
                if (_vm.Aid != _aid) { _aid = _vm.Aid; if (Armed) Show(new OsdMessage(OsdMessage.IconAudio, Line(L.T("Audio"), TrackName(_vm.AudioTracks, _aid)))); }
                break;
            case nameof(PlayerViewModel.Vid):
                if (_vm.Vid != _vid) { _vid = _vm.Vid; if (Armed) Show(new OsdMessage(OsdMessage.IconVideo, Line(L.T("Video"), TrackName(_vm.VideoTracks, _vid)))); }
                break;

            case nameof(PlayerViewModel.SubDelay):
                if (Math.Abs(_vm.SubDelay - _subDelay) > 0.0005) { _subDelay = _vm.SubDelay; if (Armed) Show(new OsdMessage(OsdMessage.IconSubtitle, Delay(L.T("Subtitle delay"), _subDelay))); }
                break;
            case nameof(PlayerViewModel.AudioDelay):
                if (Math.Abs(_vm.AudioDelay - _audioDelay) > 0.0005) { _audioDelay = _vm.AudioDelay; if (Armed) Show(new OsdMessage(OsdMessage.IconAudio, Delay(L.T("Audio delay"), _audioDelay))); }
                break;
            case nameof(PlayerViewModel.SubScale):
                if (Math.Abs(_vm.SubScale - _subScale) > 0.0005) { _subScale = _vm.SubScale; if (Armed) Show(new OsdMessage(OsdMessage.IconSubtitle, L.F("Subtitle scale: {0}x", _subScale.ToString("0.00", CultureInfo.InvariantCulture)))); }
                break;
            case nameof(PlayerViewModel.SubPos):
                if (_vm.SubPos != _subPos) { _subPos = _vm.SubPos; if (Armed) Show(new OsdMessage(OsdMessage.IconSubtitle, L.F("Subtitle position: {0}", _subPos))); }
                break;

            case nameof(PlayerViewModel.Rotate):
                if (_vm.Rotate != _rotate) { _rotate = _vm.Rotate; if (Armed) Show(new OsdMessage(OsdMessage.IconRotate, L.F("Rotate: {0}°", _rotate))); }
                break;
            case nameof(PlayerViewModel.Aspect):
                if (_vm.Aspect != _aspect) { _aspect = _vm.Aspect; if (Armed) Show(new OsdMessage(OsdMessage.IconAspect, Line(L.T("Aspect ratio"), _aspect is "no" or "-1" or "-1.000000" ? L.T("Auto") : _aspect))); }
                break;
            case nameof(PlayerViewModel.Crop):
                if (_vm.Crop != _crop) { _crop = _vm.Crop; if (Armed) Show(new OsdMessage(OsdMessage.IconCrop, Line(L.T("Crop"), _crop.Length == 0 ? L.T("None") : _crop))); }
                break;
            case nameof(PlayerViewModel.Deinterlace):
                if (_vm.Deinterlace != _deinterlace) { _deinterlace = _vm.Deinterlace; if (Armed) Show(new OsdMessage(OsdMessage.IconVideo, Line(L.T("Deinterlace"), L.T(_deinterlace ? "On" : "Off")))); }
                break;
            case nameof(PlayerViewModel.VideoZoom):
                if (Math.Abs(_vm.VideoZoom - _zoom) > 0.001) { _zoom = _vm.VideoZoom; if (Armed) Show(new OsdMessage(OsdMessage.IconZoom, L.F("Zoom: {0}%", (int)Math.Round(Math.Pow(2, _zoom) * 100)))); }
                break;

            case nameof(PlayerViewModel.Brightness): Eq(ref _brightness, _vm.Brightness, "Brightness", OsdMessage.IconBrightness); break;
            case nameof(PlayerViewModel.Contrast): Eq(ref _contrast, _vm.Contrast, "Contrast", OsdMessage.IconColor); break;
            case nameof(PlayerViewModel.Saturation): Eq(ref _saturation, _vm.Saturation, "Saturation", OsdMessage.IconColor); break;
            case nameof(PlayerViewModel.Gamma): Eq(ref _gamma, _vm.Gamma, "Gamma", OsdMessage.IconColor); break;
            case nameof(PlayerViewModel.Hue): Eq(ref _hue, _vm.Hue, "Hue", OsdMessage.IconColor); break;

            case nameof(PlayerViewModel.Shuffle):
                if (_vm.Shuffle != _shuffle) { _shuffle = _vm.Shuffle; if (Armed) Show(new OsdMessage(OsdMessage.IconShuffle, Line(L.T("Shuffle"), L.T(_shuffle ? "On" : "Off")))); }
                break;
            case nameof(PlayerViewModel.LoopFile):
            case nameof(PlayerViewModel.LoopPlaylist):
                if (_vm.LoopFile != _loopFile || _vm.LoopPlaylist != _loopPlaylist)
                {
                    _loopFile = _vm.LoopFile; _loopPlaylist = _vm.LoopPlaylist;
                    if (Armed) Show(new OsdMessage(_loopFile ? OsdMessage.IconLoopOne : OsdMessage.IconLoop,
                        (_loopFile ? L.T("Loop Single File") : _loopPlaylist ? L.T("Loop Playlist") : L.T("Disable Looping"))));
                }
                break;

            case nameof(PlayerViewModel.AbLoopA):
            case nameof(PlayerViewModel.AbLoopB):
                if (!SameTime(_vm.AbLoopA, _abA) || !SameTime(_vm.AbLoopB, _abB))
                {
                    _abA = _vm.AbLoopA; _abB = _vm.AbLoopB;
                    if (!Armed) break;
                    if (double.IsNaN(_abA) && double.IsNaN(_abB)) Show(new OsdMessage(OsdMessage.IconLoop, L.T("AB-Loop: Cleared")));
                    else if (double.IsNaN(_abB)) Show(new OsdMessage(OsdMessage.IconLoop, L.T("AB-Loop: A")));
                    else Show(new OsdMessage(OsdMessage.IconLoop, L.T("AB-Loop: B")));
                }
                break;

            case nameof(PlayerViewModel.Chapter):
                if (_vm.Chapter != _chapter)
                {
                    _chapter = _vm.Chapter;
                    if (Armed && _chapter >= 0 && _chapter < _vm.Chapters.Count)
                        Show(new OsdMessage(OsdMessage.IconChapter, Line(L.T("Chapter"), _vm.Chapters[(int)_chapter].Display)));
                }
                break;
        }
    }

    private void Eq(ref long previous, long current, string label, string icon)
    {
        if (current == previous) return;
        previous = current;
        if (Armed) Show(new OsdMessage(icon, L.F("{0}: {1}", L.T(label), Signed(current)), null, (current + 100) / 200.0));
    }

    private static bool SameTime(double a, double b) => double.IsNaN(a) && double.IsNaN(b) || Math.Abs(a - b) < 0.0005;
}
