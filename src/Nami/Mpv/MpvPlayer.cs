using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Nami.Mpv;

public sealed record MpvLogMessage(string Prefix, string Level, string Text);

public sealed record MpvEndFile(bool IsError, int ErrorCode, bool IsEof);

/// <summary>
/// Owns one mpv core configured for D3D11 composition output. All callbacks are
/// marshalled to the DispatcherQueue given at construction, so subscribers may
/// touch XAML freely.
/// </summary>
public sealed unsafe class MpvPlayer : IDisposable
{
    private readonly DispatcherQueue _ui;
    private nint _handle;
    private readonly Thread _eventThread;
    private readonly HashSet<string> _observed = new(StringComparer.Ordinal);
    private ulong _nextObserveId = 1;
    private bool _disposed;

    /// <summary>Raised with the IDXGISwapChain pointer mpv created (0 when the VO went away).</summary>
    public event Action<nint>? SwapChainChanged;
    public event Action<string, object?>? PropertyChanged;
    public event Action<MpvLogMessage>? LogMessage;
    public event Action? FileLoaded;
    public event Action? PlaybackRestart;
    /// <summary>A seek was initiated (keyboard, slider, script…).</summary>
    public event Action? SeekStarted;
    /// <summary>mpv "script-message" arguments.</summary>
    public event Action<string[]>? ClientMessage;
    /// <summary>A screenshot was written to the given path.</summary>
    public event Action<string>? ScreenshotSaved;
    public event Action<MpvEndFile>? EndFile;
    public event Action? Shutdown;
    /// <summary>Raised on the event thread when a hook fires; the handler runs before mpv continues.</summary>
    public event Action<string>? Hook;

    /// <summary>Raw mpv handle for the few call sites that need a property the wrapper does not expose.</summary>
    internal nint Handle => _handle;

    public MpvPlayer(DispatcherQueue ui, int initialWidth, int initialHeight, string configDir,
                     Services.AppSettings settings, IReadOnlyList<(string name, string value)> extraOptions)
    {
        _ui = ui;
        _handle = LibMpv.mpv_create();
        if (_handle == 0)
            throw new InvalidOperationException("mpv_create failed");

        // --- options that must be set before mpv_initialize ---
        Option("config", "yes");
        Option("config-dir", configDir);
        Option("terminal", "no");
        Option("msg-level", "all=v");
        Option("log-file", Path.Combine(configDir, "mpv.log"));

        // Video: mpv renders into a DXGI composition swapchain that we hand to XAML.
        Option("vo", "gpu-next");
        Option("gpu-context", "d3d11");
        Option("d3d11-output-mode", "composition");
        Option("d3d11-composition-size", $"{Math.Max(1, initialWidth)}x{Math.Max(1, initialHeight)}");
        Option("hwdec", "auto-safe");
        Option("force-window", "immediate");   // create the VO (and thus the swapchain) right away
        Option("idle", "yes");
        Option("keep-open", "yes");

        // Keys are forwarded from XAML via the "keypress" command, so mpv's default
        // bindings and the user's input.conf keep working.
        Option("input-default-bindings", "yes");
        Option("input-vo-keyboard", "no");
        Option("osc", "no");
        Option("cursor-autohide", "no");

        // The app draws its own OSD (see Controls/Osd); mpv's is off. Subtitles are unaffected.
        Option("osd-level", "0");
        Option("osd-bar", "no");

        Option("screenshot-directory", string.IsNullOrEmpty(settings.ScreenshotDirectory)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures) : settings.ScreenshotDirectory);
        Option("screenshot-template", "Nami-%F-%P");
        Option("screenshot-format", settings.ScreenshotFormat);
        Option("screenshot-jpeg-quality", "92");
        Option("screenshot-png-compression", "5");

        foreach (var (name, value) in extraOptions)
        {
            int r = LibMpv.mpv_set_option_string(_handle, name, value);
            if (r < 0) App.Log($"--mpv-{name}={value}: {LibMpv.ErrorString(r)}");
        }

        Option("audio-client-name", "Nami");

        // Resume where we left off (IINA: "resume last playback position").
        Option("save-position-on-quit", settings.ResumePlayback ? "yes" : "no");
        Option("resume-playback", settings.ResumePlayback ? "yes" : "no");
        Option("watch-later-dir", Path.Combine(configDir, "watch_later"));
        Option("watch-later-options", "start");

        // Windows media keys / system media transport controls (off by default in libmpv).
        Option("media-controls", "yes");

        // yt-dlp: mpv's ytdl_hook looks in the config directory, where we drop yt-dlp.exe.
        Option("ytdl", "yes");
        Option("ytdl-format", settings.YtdlFormat);

        // "info" so screenshot confirmations ("[screenshot] Screenshot: 'path'") reach us.
        MpvException.ThrowIfError(LibMpv.mpv_request_log_messages(_handle, "info"), "request_log_messages");
        MpvException.ThrowIfError(LibMpv.mpv_initialize(_handle), "mpv_initialize");

        Observe("display-swapchain", MpvFormat.Int64);
        MpvException.ThrowIfError(LibMpv.mpv_hook_add(_handle, 1, "on_unload", 0), "hook on_unload");

        _eventThread = new Thread(EventLoop) { Name = "mpv events", IsBackground = true };
        _eventThread.Start();
    }

    public static string ApiVersion
    {
        get
        {
            uint v = LibMpv.mpv_client_api_version();
            return $"{v >> 16}.{v & 0xffff}";
        }
    }

    // ------------------------------------------------------------------ options / properties

    private void Option(string name, string value)
        => MpvException.ThrowIfError(LibMpv.mpv_set_option_string(_handle, name, value), $"set option {name}");

    public void Observe(string name, MpvFormat format)
    {
        lock (_observed)
        {
            if (!_observed.Add(name)) return;
        }
        MpvException.ThrowIfError(LibMpv.mpv_observe_property(_handle, _nextObserveId++, name, format), $"observe {name}");
    }

    public void ObserveDouble(string name) => Observe(name, MpvFormat.Double);
    public void ObserveInt64(string name) => Observe(name, MpvFormat.Int64);
    public void ObserveFlag(string name) => Observe(name, MpvFormat.Flag);
    public void ObserveString(string name) => Observe(name, MpvFormat.String);
    public void ObserveNode(string name) => Observe(name, MpvFormat.Node);

    public void SetProperty(string name, string value)
        => MpvException.ThrowIfError(LibMpv.mpv_set_property_string(_handle, name, value), $"set {name}");

    public void SetProperty(string name, double value)
        => MpvException.ThrowIfError(LibMpv.mpv_set_property(_handle, name, MpvFormat.Double, &value), $"set {name}");

    public void SetProperty(string name, long value)
        => MpvException.ThrowIfError(LibMpv.mpv_set_property(_handle, name, MpvFormat.Int64, &value), $"set {name}");

    public void SetProperty(string name, bool value)
    {
        int flag = value ? 1 : 0;
        MpvException.ThrowIfError(LibMpv.mpv_set_property(_handle, name, MpvFormat.Flag, &flag), $"set {name}");
    }

    public double? GetDouble(string name)
    {
        double v;
        return LibMpv.mpv_get_property(_handle, name, MpvFormat.Double, &v) >= 0 ? v : null;
    }

    public long? GetInt64(string name)
    {
        long v;
        return LibMpv.mpv_get_property(_handle, name, MpvFormat.Int64, &v) >= 0 ? v : null;
    }

    public bool? GetFlag(string name)
    {
        int v;
        return LibMpv.mpv_get_property(_handle, name, MpvFormat.Flag, &v) >= 0 ? v != 0 : null;
    }

    /// <summary>Read a property as an mpv_node tree converted to managed objects (lists / dictionaries).</summary>
    /// <summary>Run a command and return its result node as managed objects (null on failure).</summary>
    public object? CommandNode(params string[] args)
    {
        if (_handle == 0) return null;
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            MpvNode node;
            fixed (nint* p = ptrs)
                if (LibMpv.mpv_command_ret(_handle, (byte**)p, &node) < 0) return null;
            try { return MpvNodeReader.ToManaged(&node); }
            finally { LibMpv.mpv_free_node_contents(&node); }
        }
        finally { foreach (var p in ptrs) if (p != 0) Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>The current video frame as bgr0 rows (top-down), or null.</summary>
    public (byte[] pixels, int width, int height, int stride)? ScreenshotRaw()
    {
        if (CommandNode("screenshot-raw", "video") is not Dictionary<string, object?> d) return null;
        if (d.TryGetValue("data", out var data) && data is byte[] px
            && d.TryGetValue("w", out var w) && w is long lw
            && d.TryGetValue("h", out var h) && h is long lh
            && d.TryGetValue("stride", out var st) && st is long ls
            && d.TryGetValue("format", out var f) && f is string fmt && fmt == "bgr0")
            return (px, (int)lw, (int)lh, (int)ls);
        return null;
    }

    public object? GetNode(string name)
    {
        MpvNode node;
        if (LibMpv.mpv_get_property(_handle, name, MpvFormat.Node, &node) < 0) return null;
        try { return MpvNodeReader.ToManaged(&node); }
        finally { LibMpv.mpv_free_node_contents(&node); }
    }

    public string? GetString(string name)
    {
        byte* p = LibMpv.mpv_get_property_string(_handle, name);
        if (p == null) return null;
        try { return LibMpv.Utf8(p); }
        finally { LibMpv.mpv_free(p); }
    }

    // ------------------------------------------------------------------ commands

    public void Command(params string[] args)
        => MpvException.ThrowIfError(RunCommand(args), $"command {args[0]}");

    /// <summary>Like Command, but swallows errors (for best-effort UI actions).</summary>
    public bool TryCommand(params string[] args) => RunCommand(args) >= 0;

    private int RunCommand(string[] args)
    {
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++)
                ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (nint* p = ptrs)
                return LibMpv.mpv_command(_handle, (byte**)p);
        }
        finally
        {
            for (int i = 0; i < args.Length; i++)
                if (ptrs[i] != 0) Marshal.FreeCoTaskMem(ptrs[i]);
        }
    }

    public void LoadFile(string pathOrUrl, bool append = false)
        => Command("loadfile", pathOrUrl, append ? "append-play" : "replace");

    public void TogglePause() => Command("cycle", "pause");
    public void SetPause(bool paused) => SetProperty("pause", paused);

    public void Seek(double seconds, bool relative = true, bool exact = false)
        => Command("seek", seconds.ToString(CultureInfo.InvariantCulture),
                   relative ? (exact ? "relative+exact" : "relative") : (exact ? "absolute+exact" : "absolute"));

    public void Stop() => Command("stop");

    /// <summary>Resize the composition swapchain (physical pixels).</summary>
    public void SetOutputSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        SetProperty("d3d11-composition-size", $"{width}x{height}");
    }

    // ------------------------------------------------------------------ event loop

    private void EventLoop()
    {
        while (true)
        {
            MpvEvent* ev = LibMpv.mpv_wait_event(_handle, 1e20);
            if (ev == null) continue;

            switch (ev->EventId)
            {
                case MpvEventId.Shutdown:
                    Post(() => Shutdown?.Invoke());
                    return;

                case MpvEventId.LogMessage:
                {
                    var m = (MpvEventLogMessage*)ev->Data;
                    var msg = new MpvLogMessage(LibMpv.Utf8(m->Prefix), LibMpv.Utf8(m->Level), LibMpv.Utf8(m->Text).TrimEnd());
                    if (msg.Level is "error" or "warn" or "fatal")
                        Debug.WriteLine($"[mpv/{msg.Prefix}] {msg.Level}: {msg.Text}");
                    if (msg.Prefix == "screenshot" && msg.Text.StartsWith("Screenshot: '", StringComparison.Ordinal))
                    {
                        string path = msg.Text[13..].TrimEnd('\'');
                        Post(() => ScreenshotSaved?.Invoke(path));
                    }
                    Post(() => LogMessage?.Invoke(msg));
                    break;
                }

                case MpvEventId.PropertyChange:
                {
                    var p = (MpvEventProperty*)ev->Data;
                    string name = LibMpv.Utf8(p->Name);
                    object? value = ReadPropertyData(p);
                    if (name == "display-swapchain")
                    {
                        nint sc = value is long l ? (nint)l : 0;
                        Post(() => SwapChainChanged?.Invoke(sc));
                    }
                    Post(() => PropertyChanged?.Invoke(name, value));
                    break;
                }

                case MpvEventId.FileLoaded:
                    Post(() => FileLoaded?.Invoke());
                    break;

                case MpvEventId.PlaybackRestart:
                    Post(() => PlaybackRestart?.Invoke());
                    break;

                case MpvEventId.Seek:
                    Post(() => SeekStarted?.Invoke());
                    break;

                case MpvEventId.ClientMessage:
                {
                    var cm = (MpvEventClientMessage*)ev->Data;
                    var args = new string[cm->NumArgs];
                    for (int i = 0; i < cm->NumArgs; i++) args[i] = LibMpv.Utf8(cm->Args[i]);
                    Post(() => ClientMessage?.Invoke(args));
                    break;
                }

                case MpvEventId.Hook:
                {
                    var h = (MpvEventHook*)ev->Data;
                    string name = LibMpv.Utf8(h->Name);
                    ulong id = h->Id;
                    try { Hook?.Invoke(name); }
                    finally { LibMpv.mpv_hook_continue(_handle, id); }
                    break;
                }

                case MpvEventId.EndFile:
                {
                    var e = (MpvEventEndFile*)ev->Data;
                    var info = new MpvEndFile(e->Reason == MpvEndFileReason.Error, e->Error, e->Reason == MpvEndFileReason.Eof);
                    Post(() => EndFile?.Invoke(info));
                    break;
                }
            }
        }
    }

    private static object? ReadPropertyData(MpvEventProperty* p)
    {
        if (p->Data == null) return null;
        return p->Format switch
        {
            MpvFormat.String or MpvFormat.OsdString => LibMpv.Utf8(*(byte**)p->Data),
            MpvFormat.Flag => *(int*)p->Data != 0,
            MpvFormat.Int64 => *(long*)p->Data,
            MpvFormat.Double => *(double*)p->Data,
            MpvFormat.Node => MpvNodeReader.ToManaged((MpvNode*)p->Data),
            _ => null,
        };
    }

    private void Post(Action action)
    {
        // Returns false only when the dispatcher is shutting down; nothing to do then.
        _ui.TryEnqueue(() => action());
    }

    // ------------------------------------------------------------------ teardown

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        nint h = _handle;
        if (h == 0) return;

        // Ask the core to quit so the event thread sees SHUTDOWN and exits,
        // then tear the core down. mpv_terminate_destroy blocks until done.
        LibMpv.mpv_command_string(h, "quit");
        _eventThread.Join(TimeSpan.FromSeconds(5));
        _handle = 0;
        LibMpv.mpv_terminate_destroy(h);
    }
}
