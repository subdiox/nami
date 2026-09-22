using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Nami.Mpv;
using Nami.Services;

namespace Nami.Player;

/// <summary>
/// UI-facing state of the player. Mirrors mpv properties (updated on the UI thread)
/// and exposes the commands the UI needs. Attach() is called once the mpv core exists.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private MpvPlayer? _player;
    private readonly Queue<Action<MpvPlayer>> _pending = new();
    private int _posTick;

    public MpvPlayer? Player => _player;
    public bool IsAttached => _player is not null;

    // ---- playback state ---------------------------------------------------------
    [ObservableProperty] public partial bool Paused { get; set; } = true;
    [ObservableProperty] public partial bool Idle { get; set; } = true;
    [ObservableProperty] public partial double TimePos { get; set; }
    [ObservableProperty] public partial double Duration { get; set; }
    [ObservableProperty] public partial double CacheDuration { get; set; }
    [ObservableProperty] public partial bool Seeking { get; set; }
    [ObservableProperty] public partial bool PausedForCache { get; set; }
    [ObservableProperty] public partial bool EofReached { get; set; }
    [ObservableProperty] public partial double Volume { get; set; } = 100;
    [ObservableProperty] public partial bool Muted { get; set; }
    [ObservableProperty] public partial double Speed { get; set; } = 1;
    [ObservableProperty] public partial string MediaTitle { get; set; } = "";
    [ObservableProperty] public partial string? FilePath { get; set; }
    [ObservableProperty] public partial bool Fullscreen { get; set; }
    [ObservableProperty] public partial bool OnTop { get; set; }
    [ObservableProperty] public partial long VideoWidth { get; set; }
    [ObservableProperty] public partial long VideoHeight { get; set; }
    /// <summary>Raised after both VideoWidth and VideoHeight are updated.</summary>
    [ObservableProperty] public partial VideoSize VideoSize { get; set; }
    [ObservableProperty] public partial long Chapter { get; set; } = -1;
    [ObservableProperty] public partial double AudioDelay { get; set; }
    [ObservableProperty] public partial double SubDelay { get; set; }
    [ObservableProperty] public partial double SubScale { get; set; } = 1;
    [ObservableProperty] public partial long SubPos { get; set; } = 100;
    [ObservableProperty] public partial string Aspect { get; set; } = "-1";
    [ObservableProperty] public partial long Rotate { get; set; }
    [ObservableProperty] public partial bool Deinterlace { get; set; }
    [ObservableProperty] public partial long Brightness { get; set; }
    [ObservableProperty] public partial long Contrast { get; set; }
    [ObservableProperty] public partial long Saturation { get; set; }
    [ObservableProperty] public partial long Gamma { get; set; }
    [ObservableProperty] public partial long Hue { get; set; }
    [ObservableProperty] public partial string Vid { get; set; } = "auto";
    [ObservableProperty] public partial string Aid { get; set; } = "auto";
    [ObservableProperty] public partial string Sid { get; set; } = "auto";
    [ObservableProperty] public partial string SecondarySid { get; set; } = "no";
    [ObservableProperty] public partial string HwdecCurrent { get; set; } = "";
    [ObservableProperty] public partial long PlaylistPos { get; set; } = -1;
    [ObservableProperty] public partial long PlaylistCount { get; set; }
    [ObservableProperty] public partial string MetaTitle { get; set; } = "";
    [ObservableProperty] public partial string MetaArtist { get; set; } = "";
    [ObservableProperty] public partial string MetaAlbum { get; set; } = "";
    [ObservableProperty] public partial bool Shuffle { get; set; }
    [ObservableProperty] public partial bool LoopFile { get; set; }
    [ObservableProperty] public partial bool LoopPlaylist { get; set; }
    /// <summary>True when the current file has no real video (audio only, or cover art only).</summary>
    [ObservableProperty] public partial bool IsAudioOnly { get; set; }
    [ObservableProperty] public partial bool MusicMode { get; set; }
    [ObservableProperty] public partial double AbLoopA { get; set; } = double.NaN;
    [ObservableProperty] public partial double AbLoopB { get; set; } = double.NaN;
    [ObservableProperty] public partial string AudioDevice { get; set; } = "auto";
    [ObservableProperty] public partial string Crop { get; set; } = "";
    [ObservableProperty] public partial string VideoFilters { get; set; } = "";
    [ObservableProperty] public partial double VideoZoom { get; set; }
    public ObservableCollection<AudioDeviceInfo> AudioDevices { get; } = [];

    public ObservableCollection<TrackInfo> VideoTracks { get; } = [];
    public ObservableCollection<TrackInfo> AudioTracks { get; } = [];
    public ObservableCollection<TrackInfo> SubTracks { get; } = [];
    public ObservableCollection<PlaylistItem> Playlist { get; } = [];
    public ObservableCollection<ChapterInfo> Chapters { get; } = [];

    // ---- UI state (not mpv) --------------------------------------------------------
    [ObservableProperty] public partial SidebarKind Sidebar { get; set; } = SidebarKind.None;
    [ObservableProperty] public partial int SettingsTab { get; set; }
    [ObservableProperty] public partial int PlaylistTab { get; set; }

    public History History { get; } = History.Load();
    public ThumbnailGenerator Thumbnails { get; } = new();
    private string? _autoLoadedFolder;

    public event Action? FileLoaded;
    public event Action? PlaybackRestart;
    public event Action<string>? Error;
    public event Action? Shutdown;

    // ==============================================================================

    public void Attach(MpvPlayer player)
    {
        _player = player;
        player.PropertyChanged += OnMpvProperty;
        player.FileLoaded += OnFileLoaded;
        player.PlaybackRestart += () =>
        {
            PlaybackRestart?.Invoke();
            StartThumbnails();
        };
        player.Hook += name =>
        {
            // Runs on the mpv event thread, before the file is unloaded.
            if (name == "on_unload" && App.Settings.ResumePlayback)
                player.TryCommand("write-watch-later-config");
        };
        player.EndFile += e => { if (e.IsError) Error?.Invoke(LibMpv.ErrorString(e.ErrorCode)); };
        player.Shutdown += () => Shutdown?.Invoke();

        foreach (var (name, fmt) in ObservedProperties)
            player.Observe(name, fmt);

        // Restore persisted state.
        var s = App.Settings;
        s.Subtitles.Apply(player);
        if (s.EqEnabled) ApplyEq(s.EqGains, true);
        // Command-line --mpv-volume / --mpv-mute win over the remembered values.
        bool cliVolume = MpvPlayer.ExtraOptions.Any(o => o.name is "volume" or "mute");
        if (s.RememberVolume && !cliVolume)
        {
            Try(() => player.SetProperty("volume", s.Volume));
            Try(() => player.SetProperty("mute", s.Muted));
        }

        while (_pending.Count > 0) _pending.Dequeue()(player);
        OnPropertyChanged(nameof(IsAttached));
    }

    private void OnFileLoaded()
    {
        string? path = _player?.GetString("path");
        if (!string.IsNullOrEmpty(path))
        {
            if (App.Settings.KeepHistory)
                History.Touch(path, _player?.GetString("media-title") ?? "", _player?.GetDouble("duration") ?? 0);
            AutoLoadFolder(path);
        }
        FileLoaded?.Invoke();
    }

    /// <summary>IINA: when a single local file is opened, queue the rest of its folder.</summary>
    private void AutoLoadFolder(string path)
    {
        if (!App.Settings.AutoLoadFolder || _player is null) return;
        if (path.Contains("://") || !File.Exists(path)) return;
        if ((_player.GetInt64("playlist-count") ?? 0) != 1) return;
        string? dir = Path.GetDirectoryName(path);
        if (dir is null || string.Equals(dir, _autoLoadedFolder, StringComparison.OrdinalIgnoreCase)) return;
        _autoLoadedFolder = dir;

        var siblings = FolderPlaylist.Siblings(path);
        int index = siblings.FindIndex(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        if (siblings.Count <= 1 || index < 0) return;
        foreach (var f in siblings)
        {
            if (string.Equals(f, path, StringComparison.OrdinalIgnoreCase)) continue;
            _player.TryCommand("loadfile", f, "append");
        }
        // The current file is at index 0; move it to its natural position.
        if (index > 0) _player.TryCommand("playlist-move", "0", (index + 1).ToString());
    }

    private string? _thumbPath;

    private void StartThumbnails()
    {
        if (!App.Settings.SeekThumbnails) { Thumbnails.Stop(); _thumbPath = null; return; }
        if (FilePath is null || FilePath == _thumbPath || !VideoSize.IsValid || Duration <= 0) return;
        _thumbPath = FilePath;
        Thumbnails.Start(FilePath, Duration, VideoSize.Aspect);
    }

    /// <summary>Called periodically / on unload so the history remembers where we were.</summary>
    public void RecordPosition()
    {
        if (!App.Settings.KeepHistory || string.IsNullOrEmpty(FilePath)) return;
        History.UpdatePosition(FilePath, TimePos, Duration);
    }

    private static readonly (string, MpvFormat)[] ObservedProperties =
    [
        ("pause", MpvFormat.Flag), ("idle-active", MpvFormat.Flag), ("time-pos", MpvFormat.Double),
        ("duration", MpvFormat.Double), ("demuxer-cache-duration", MpvFormat.Double), ("seeking", MpvFormat.Flag),
        ("paused-for-cache", MpvFormat.Flag), ("eof-reached", MpvFormat.Flag), ("volume", MpvFormat.Double),
        ("mute", MpvFormat.Flag), ("speed", MpvFormat.Double), ("media-title", MpvFormat.String),
        ("path", MpvFormat.String), ("fullscreen", MpvFormat.Flag), ("ontop", MpvFormat.Flag),
        ("video-out-params", MpvFormat.Node), ("chapter", MpvFormat.Int64),
        ("audio-delay", MpvFormat.Double), ("sub-delay", MpvFormat.Double), ("sub-scale", MpvFormat.Double),
        ("sub-pos", MpvFormat.Int64), ("video-aspect-override", MpvFormat.String), ("video-rotate", MpvFormat.Int64),
        ("deinterlace", MpvFormat.Flag), ("brightness", MpvFormat.Int64), ("contrast", MpvFormat.Int64),
        ("saturation", MpvFormat.Int64), ("gamma", MpvFormat.Int64), ("hue", MpvFormat.Int64),
        ("vid", MpvFormat.String), ("aid", MpvFormat.String), ("sid", MpvFormat.String),
        ("secondary-sid", MpvFormat.String), ("hwdec-current", MpvFormat.String),
        ("playlist-pos", MpvFormat.Int64), ("playlist-count", MpvFormat.Int64),
        ("track-list", MpvFormat.Node), ("playlist", MpvFormat.Node), ("chapter-list", MpvFormat.Node),
        ("metadata", MpvFormat.Node), ("shuffle", MpvFormat.Flag), ("loop-file", MpvFormat.String), ("loop-playlist", MpvFormat.String),
        ("ab-loop-a", MpvFormat.String), ("ab-loop-b", MpvFormat.String), ("audio-device", MpvFormat.String),
        ("audio-device-list", MpvFormat.Node), ("video-crop", MpvFormat.String), ("vf", MpvFormat.String), ("video-zoom", MpvFormat.Double),
    ];

    private void OnMpvProperty(string name, object? value)
    {
        switch (name)
        {
            case "pause": Paused = value is true; break;
            case "idle-active": Idle = value is true; break;
            case "time-pos":
                TimePos = value as double? ?? 0;
                if ((++_posTick & 31) == 0) RecordPosition();   // roughly every few seconds
                break;
            case "duration": Duration = value as double? ?? 0; break;
            case "demuxer-cache-duration": CacheDuration = value as double? ?? 0; break;
            case "seeking": Seeking = value is true; break;
            case "paused-for-cache": PausedForCache = value is true; break;
            case "eof-reached": EofReached = value is true; break;
            case "volume":
                Volume = value as double? ?? Volume;
                PersistVolume();
                break;
            case "mute":
                Muted = value is true;
                PersistVolume();
                break;
            case "speed": Speed = value as double? ?? 1; break;
            case "media-title": MediaTitle = value as string ?? ""; break;
            case "path": FilePath = value as string; break;
            case "fullscreen": Fullscreen = value is true; break;
            case "ontop": OnTop = value is true; break;
            case "video-out-params":
            {
                // dw/dh = display size of the source with aspect correction applied
                // (unlike dwidth/dheight, which are the size of the output surface).
                if (value is Dictionary<string, object?> vp)
                {
                    long dw = Long(vp, "dw"), dh = Long(vp, "dh");
                    long rot = Long(vp, "rotate");
                    App.Log($"video-out-params: w={Long(vp, "w")} h={Long(vp, "h")} dw={dw} dh={dh} rotate={rot} aspect={(vp.TryGetValue("aspect", out var asp) ? asp : null)}");
                    if (rot % 180 != 0) (dw, dh) = (dh, dw);
                    if (dw > 0 && dh > 0)
                    {
                        VideoWidth = dw;
                        VideoHeight = dh;
                        VideoSize = new VideoSize(dw, dh);
                    }
                }
                else
                {
                    VideoWidth = 0;
                    VideoHeight = 0;
                    VideoSize = default;
                }
                break;
            }
            case "chapter": Chapter = value as long? ?? -1; break;
            case "audio-delay": AudioDelay = value as double? ?? 0; break;
            case "sub-delay": SubDelay = value as double? ?? 0; break;
            case "sub-scale": SubScale = value as double? ?? 1; break;
            case "sub-pos": SubPos = value as long? ?? 100; break;
            case "video-aspect-override": Aspect = value as string ?? "-1"; break;
            case "video-rotate": Rotate = value as long? ?? 0; break;
            case "deinterlace": Deinterlace = value is true; break;
            case "brightness": Brightness = value as long? ?? 0; break;
            case "contrast": Contrast = value as long? ?? 0; break;
            case "saturation": Saturation = value as long? ?? 0; break;
            case "gamma": Gamma = value as long? ?? 0; break;
            case "hue": Hue = value as long? ?? 0; break;
            case "vid": Vid = value as string ?? "auto"; break;
            case "aid": Aid = value as string ?? "auto"; break;
            case "sid": Sid = value as string ?? "auto"; break;
            case "secondary-sid": SecondarySid = value as string ?? "no"; break;
            case "hwdec-current": HwdecCurrent = value as string ?? ""; break;
            case "playlist-pos": PlaylistPos = value as long? ?? -1; break;
            case "playlist-count": PlaylistCount = value as long? ?? 0; break;
            case "track-list": UpdateTracks(value as List<object?>); break;
            case "playlist": UpdatePlaylist(value as List<object?>); break;
            case "chapter-list": UpdateChapters(value as List<object?>); break;
            case "metadata":
            {
                var d = value as Dictionary<string, object?>;
                MetaTitle = MetaValue(d, "title");
                MetaArtist = MetaValue(d, "artist", "album_artist");
                MetaAlbum = MetaValue(d, "album");
                break;
            }
            case "shuffle": Shuffle = value is true; break;
            case "ab-loop-a": AbLoopA = ParseTime(value as string); break;
            case "ab-loop-b": AbLoopB = ParseTime(value as string); break;
            case "audio-device": AudioDevice = value as string ?? "auto"; break;
            case "audio-device-list": UpdateAudioDevices(value as List<object?>); break;
            case "video-crop": Crop = value as string ?? ""; break;
            case "vf": VideoFilters = value as string ?? ""; break;
            case "video-zoom": VideoZoom = value as double? ?? 0; break;
            case "loop-file": LoopFile = value is string lf && lf != "no"; break;
            case "loop-playlist": LoopPlaylist = value is string lp && lp != "no"; break;
        }
    }

    private void PersistVolume()
    {
        var s = App.Settings;
        if (!s.RememberVolume) return;
        if (Math.Abs(s.Volume - Volume) < 0.01 && s.Muted == Muted) return;
        s.Volume = Volume;
        s.Muted = Muted;
        s.Save();
    }

    // ---- node parsing -------------------------------------------------------------

    private static double ParseTime(string? s)
        => s is not null && s != "no" && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN;

    private void UpdateAudioDevices(List<object?>? list)
    {
        var items = new List<AudioDeviceInfo>();
        if (list is not null)
            foreach (var item in list)
                if (item is Dictionary<string, object?> d)
                    items.Add(new AudioDeviceInfo(Str(d, "name") ?? "auto", Str(d, "description") ?? ""));
        Replace(AudioDevices, items);
    }

    private static string MetaValue(Dictionary<string, object?>? d, params string[] keys)
    {
        if (d is null) return "";
        foreach (var k in keys)
            foreach (var kv in d)
                if (string.Equals(kv.Key, k, StringComparison.OrdinalIgnoreCase) && kv.Value is string s && s.Length > 0)
                    return s;
        return "";
    }

    private static string? Str(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) ? v as string : null;
    private static long Long(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v is long l ? l : 0;
    private static double Dbl(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v is double x ? x : 0;
    private static bool Flag(Dictionary<string, object?> d, string k) => d.TryGetValue(k, out var v) && v is true;

    private void UpdateTracks(List<object?>? list)
    {
        var video = new List<TrackInfo>();
        var audio = new List<TrackInfo>();
        var sub = new List<TrackInfo>();
        if (list is not null)
        {
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object?> d) continue;
                var t = new TrackInfo(Long(d, "id"), Str(d, "type") ?? "", Str(d, "title"), Str(d, "lang"),
                    Flag(d, "selected"), Flag(d, "external"), Str(d, "codec"), Flag(d, "default"), Flag(d, "albumart"));
                switch (t.Type)
                {
                    case "video": video.Add(t); break;
                    case "audio": audio.Add(t); break;
                    case "sub": sub.Add(t); break;
                }
            }
        }
        Replace(VideoTracks, video);
        Replace(AudioTracks, audio);
        Replace(SubTracks, sub);
        IsAudioOnly = audio.Count > 0 && video.All(t => t.AlbumArt);
    }

    private void UpdatePlaylist(List<object?>? list)
    {
        var items = new List<PlaylistItem>();
        if (list is not null)
        {
            int i = 0;
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object?> d) continue;
                items.Add(new PlaylistItem(i++, Long(d, "id"), Str(d, "filename") ?? "", Str(d, "title"),
                    Flag(d, "current"), Flag(d, "playing")));
            }
        }
        Replace(Playlist, items);
    }

    private void UpdateChapters(List<object?>? list)
    {
        var items = new List<ChapterInfo>();
        if (list is not null)
        {
            int i = 0;
            foreach (var item in list)
            {
                if (item is not Dictionary<string, object?> d) continue;
                items.Add(new ChapterInfo(i++, Str(d, "title") ?? "", Dbl(d, "time")));
            }
        }
        Replace(Chapters, items);
    }

    private static void Replace<T>(ObservableCollection<T> target, List<T> source)
    {
        if (target.Count == source.Count && target.SequenceEqual(source)) return;
        target.Clear();
        foreach (var x in source) target.Add(x);
    }

    // ---- commands ------------------------------------------------------------------

    private void Run(Action<MpvPlayer> action)
    {
        if (_player is null) { _pending.Enqueue(action); return; }
        try { action(_player); }
        catch (MpvException ex) { Error?.Invoke(ex.Message); }
    }

    private static void Try(Action a) { try { a(); } catch (MpvException) { } }

    private static string Inv(double v) => v.ToString(CultureInfo.InvariantCulture);

    public void Open(string pathOrUrl, bool append = false)
    {
        if (!append) _autoLoadedFolder = null;
        Run(p => p.LoadFile(pathOrUrl, append));
    }
    public void OpenMany(IEnumerable<string> paths, bool append = false)
    {
        bool first = !append;
        foreach (var path in paths) { Open(path, append: !first); first = false; }
    }

    public void TogglePause() => Run(p => p.TogglePause());
    public void Play() => Run(p => p.SetPause(false));
    public void Pause() => Run(p => p.SetPause(true));
    public void Stop() => Run(p => p.Stop());
    public void SeekRelative(double seconds) => Run(p => p.Seek(seconds, relative: true));
    public void SeekAbsolute(double seconds, bool exact = false) => Run(p => p.Seek(seconds, relative: false, exact: exact));
    public void FrameStep() => Run(p => p.Command("frame-step"));
    public void FrameBackStep() => Run(p => p.Command("frame-back-step"));
    public void SetVolume(double v) => Run(p => p.SetProperty("volume", Math.Clamp(v, 0, 130)));
    public void ToggleMute() => Run(p => p.TryCommand("cycle", "mute"));
    public void SetSpeed(double v) => Run(p => p.SetProperty("speed", Math.Clamp(v, 0.1, 100)));
    public void PlaylistNext() => Run(p => p.TryCommand("playlist-next"));
    public void PlaylistPrev() => Run(p => p.TryCommand("playlist-prev"));
    public void PlaylistPlayIndex(int index) => Run(p => p.TryCommand("playlist-play-index", index.ToString()));
    public void PlaylistRemove(int index) => Run(p => p.TryCommand("playlist-remove", index.ToString()));
    public void PlaylistClear() => Run(p => p.TryCommand("playlist-clear"));
    public void PlaylistMove(int from, int to) => Run(p => p.TryCommand("playlist-move", from.ToString(), to.ToString()));
    public void SetChapter(int index) => Run(p => p.SetProperty("chapter", (long)index));
    public void SetTrack(string type, long id) => Run(p => p.SetProperty(type, id.ToString()));
    public void SetTrackOff(string type) => Run(p => p.SetProperty(type, "no"));
    public void SetAudioDelay(double s) => Run(p => p.SetProperty("audio-delay", s));
    public void SetSubDelay(double s) => Run(p => p.SetProperty("sub-delay", s));
    public void SetSubScale(double s) => Run(p => p.SetProperty("sub-scale", s));
    public void SetSubPos(long v) => Run(p => p.SetProperty("sub-pos", v));
    public void SetAspect(string v) => Run(p => p.SetProperty("video-aspect-override", v));
    public void SetRotate(long deg) => Run(p => p.SetProperty("video-rotate", deg));
    public void SetDeinterlace(bool on) => Run(p => p.SetProperty("deinterlace", on));
    public void SetEq(string name, long v) => Run(p => p.SetProperty(name, v));
    public void ResetEq() => Run(p => { foreach (var n in new[] { "brightness", "contrast", "saturation", "gamma", "hue" }) p.SetProperty(n, 0L); });
    public void ToggleFullscreen() => Run(p => p.TryCommand("cycle", "fullscreen"));
    public void SetFullscreen(bool on) => Run(p => p.SetProperty("fullscreen", on));
    public void ToggleOnTop() => Run(p => p.TryCommand("cycle", "ontop"));
    public void AddSubtitle(string path) => Run(p => p.TryCommand("sub-add", path, "select"));
    public void AddAudio(string path) => Run(p => p.TryCommand("audio-add", path, "select"));
    public void Screenshot() => Run(p => p.TryCommand("screenshot"));
    public void ShowText(string text, int ms = 1500) => Run(p => p.TryCommand("show-text", text, ms.ToString()));
    public void Keypress(string key) => Run(p => p.TryCommand("keypress", key));
    public void Quit() => Run(p => p.TryCommand("quit"));

    /// <summary>IINA-style left/right arrow buttons: halve / double the playback speed.</summary>
    public void SpeedStep(bool faster)
    {
        double s = Speed;
        double next = faster ? Math.Min(16, s * 2) : Math.Max(0.25, s / 2);
        if (faster && s < 1 && next > 1) next = 1;
        if (!faster && s > 1 && next < 1) next = 1;
        SetSpeed(next);
        if (Paused) Play();
        ShowText($"再生速度 {Fmt.Speed(next)}");
    }

    public void ToggleShuffle() => Run(p =>
    {
        bool on = !Shuffle;
        p.SetProperty("shuffle", on);
        p.TryCommand(on ? "playlist-shuffle" : "playlist-unshuffle");
        ShowText(on ? "シャッフル: オン" : "シャッフル: オフ");
    });

    /// <summary>off → playlist → file → off</summary>
    public void CycleLoop() => Run(p =>
    {
        if (!LoopPlaylist && !LoopFile) { p.SetProperty("loop-playlist", "inf"); ShowText("リピート: プレイリスト"); }
        else if (LoopPlaylist) { p.SetProperty("loop-playlist", "no"); p.SetProperty("loop-file", "inf"); ShowText("リピート: 1 曲"); }
        else { p.SetProperty("loop-file", "no"); ShowText("リピート: オフ"); }
    });

    /// <summary>mpv's ab-loop cycle: set A → set B → clear.</summary>
    public void CycleAbLoop() => Run(p => p.TryCommand("ab-loop"));
    public void ClearAbLoop() => Run(p => { p.SetProperty("ab-loop-a", "no"); p.SetProperty("ab-loop-b", "no"); });
    public void SetAudioDevice(string name) => Run(p => p.SetProperty("audio-device", name));
    public void SetCrop(string v) => Run(p => p.SetProperty("video-crop", v));
    public void ToggleFlip(bool horizontal) => Run(p => p.TryCommand("vf", "toggle", horizontal ? "hflip" : "vflip"));
    public void SetZoom(double zoom) => Run(p => p.SetProperty("video-zoom", Math.Clamp(zoom, -3, 3)));
    public void ResetZoom() => Run(p => { p.SetProperty("video-zoom", 0.0); p.SetProperty("video-pan-x", 0.0); p.SetProperty("video-pan-y", 0.0); });

    public static readonly int[] EqBands = [31, 62, 125, 250, 500, 1000, 2000, 4000, 8000, 16000];

    /// <summary>10-band equalizer via ffmpeg's equalizer filter chain (IINA does the same).</summary>
    public void ApplyEq(double[] gains, bool enabled) => Run(p =>
    {
        if (!enabled || gains.All(g => Math.Abs(g) < 0.05))
        {
            p.TryCommand("af", "remove", "@nami-eq");
            return;
        }
        var parts = new List<string>();
        for (int i = 0; i < EqBands.Length && i < gains.Length; i++)
            parts.Add($"equalizer=f={EqBands[i]}:t=o:w=1:g={gains[i].ToString("0.0", CultureInfo.InvariantCulture)}");
        string graph = "@nami-eq:lavfi=[" + string.Join(",", parts) + "]";
        p.TryCommand("af", "remove", "@nami-eq");
        p.TryCommand("af", "add", graph);
    });

    public void ToggleSidebar(SidebarKind kind) => Sidebar = Sidebar == kind ? SidebarKind.None : kind;
    public void CloseSidebar() => Sidebar = SidebarKind.None;
}

public enum SidebarKind
{
    None,
    Settings,
    Playlist,
}
