using System.Runtime.InteropServices;
using Nami.Mpv;

namespace Nami.Player;

/// <summary>A set of evenly spaced BGRA thumbnails for one file.</summary>
public sealed class ThumbnailSet
{
    public string Path { get; }
    public double Duration { get; }
    public int Width { get; }
    public int Height { get; }
    public int Count { get; }
    private readonly byte[]?[] _frames;

    public ThumbnailSet(string path, double duration, int width, int height, int count)
    {
        Path = path; Duration = duration; Width = width; Height = height; Count = count;
        _frames = new byte[count][];
    }

    public int Ready { get; private set; }

    internal void Set(int index, byte[] bgra)
    {
        _frames[index] = bgra;
        Ready++;
    }

    public int IndexFor(double time) => Math.Clamp((int)(time / Duration * Count), 0, Count - 1);

    /// <summary>Nearest generated frame at or before the requested time (null if none yet).</summary>
    public (int index, byte[] bgra)? Get(double time)
    {
        int i = IndexFor(time);
        for (int k = i; k >= 0; k--)
            if (_frames[k] is { } f) return (k, f);
        return null;
    }
}

/// <summary>
/// Generates seek-bar thumbnails in the background with a second, software-rendering
/// mpv core (IINA does the same job with ffmpeg). One file at a time; starting a new
/// file cancels the previous run.
/// </summary>
public sealed unsafe class ThumbnailGenerator : IDisposable
{
    public const int ThumbWidth = 240;
    public const int DefaultCount = 100;

    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private static readonly ManualResetEventSlim s_frameEvent = new(false);

    public ThumbnailSet? Current { get; private set; }
    public event Action<ThumbnailSet>? Progress;

    public void Start(string path, double duration, double aspect)
    {
        Stop();
        if (duration <= 0 || aspect <= 0 || path.Contains("://") || !File.Exists(path)) { Current = null; return; }
        int count = duration < 60 ? Math.Max(10, (int)duration) : DefaultCount;
        int h = Math.Max(2, (int)Math.Round(ThumbWidth / aspect / 2) * 2);
        var set = new ThumbnailSet(path, duration, ThumbWidth, h, count);
        Current = set;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _thread = new Thread(() => Run(set, ct)) { IsBackground = true, Name = "thumbnails", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _thread = null;
        _cts = null;
    }

    public void Dispose() => Stop();

    [UnmanagedCallersOnly]
    private static void OnUpdate(void* ctx) => s_frameEvent.Set();

    private void Run(ThumbnailSet set, CancellationToken ct)
    {
        nint mpv = LibMpv.mpv_create();
        if (mpv == 0) return;
        nint render = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            foreach (var (k, v) in new[]
            {
                ("vo", "libmpv"), ("hwdec", "no"), ("audio", "no"), ("sub", "no"), ("sid", "no"), ("aid", "no"),
                ("pause", "yes"), ("idle", "yes"), ("keep-open", "yes"), ("terminal", "no"), ("msg-level", "all=no"),
                ("hr-seek", "yes"), ("hr-seek-framedrop", "yes"), ("vd-lavc-fast", "yes"), ("vd-lavc-skiploopfilter", "all"),
                ("vd-lavc-threads", "2"), ("demuxer-readahead-secs", "0"), ("cache", "no"), ("ytdl", "no"),
                ("load-scripts", "no"), ("osc", "no"), ("osd-level", "0"), ("config", "no"),
                ("sws-fast", "yes"), ("sws-scaler", "fast-bilinear"), ("zimg-fast", "yes"),
            })
                LibMpv.mpv_set_option_string(mpv, k, v);
            if (LibMpv.mpv_initialize(mpv) < 0) return;

            int* size = stackalloc int[2] { set.Width, set.Height };
            nuint stride = (nuint)(set.Width * 4);
            var buffer = new byte[set.Width * set.Height * 4];
            fixed (byte* pixels = buffer)
            {
                byte* apiType = stackalloc byte[3] { (byte)'s', (byte)'w', 0 };
                var createParams = stackalloc MpvRenderParam[2];
                createParams[0] = new MpvRenderParam { Type = MpvRenderParamType.ApiType, Data = apiType };
                createParams[1] = default;
                nint rc;
                if (LibMpv.mpv_render_context_create(&rc, mpv, createParams) < 0) { App.Log("thumbnails: render context failed"); return; }
                render = rc;
                LibMpv.mpv_render_context_set_update_callback(render, &OnUpdate, null);

                // load the file
                Command(mpv, "loadfile", set.Path);
                if (!WaitFor(mpv, MpvEventId.FileLoaded, TimeSpan.FromSeconds(15), ct)) { App.Log("thumbnails: file did not load"); return; }

                byte* fmt = stackalloc byte[5] { (byte)'b', (byte)'g', (byte)'r', (byte)'a', 0 };
                var renderParams = stackalloc MpvRenderParam[5];
                renderParams[0] = new MpvRenderParam { Type = MpvRenderParamType.SwSize, Data = size };
                renderParams[1] = new MpvRenderParam { Type = MpvRenderParamType.SwFormat, Data = fmt };
                renderParams[2] = new MpvRenderParam { Type = MpvRenderParamType.SwStride, Data = &stride };
                renderParams[3] = new MpvRenderParam { Type = MpvRenderParamType.SwPointer, Data = pixels };
                renderParams[4] = default;

                for (int i = 0; i < set.Count && !ct.IsCancellationRequested; i++)
                {
                    double t = set.Duration * (i + 0.5) / set.Count;
                    s_frameEvent.Reset();
                    Command(mpv, "seek", t.ToString("F3", System.Globalization.CultureInfo.InvariantCulture), "absolute+exact");
                    if (!WaitFor(mpv, MpvEventId.PlaybackRestart, TimeSpan.FromSeconds(5), ct)) continue;
                    if (!s_frameEvent.Wait(TimeSpan.FromSeconds(2), ct)) continue;
                    ulong flags = LibMpv.mpv_render_context_update(render);
                    if ((flags & 1) == 0 && !s_frameEvent.Wait(500, ct)) continue;
                    if (LibMpv.mpv_render_context_render(render, renderParams) < 0) continue;
                    set.Set(i, (byte[])buffer.Clone());
                    Progress?.Invoke(set);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"thumbnails: {ex}"); }
        finally
        {
            App.Log($"thumbnails: {set.Ready}/{set.Count} frames in {sw.ElapsedMilliseconds} ms ({set.Width}x{set.Height})");
            if (render != 0) LibMpv.mpv_render_context_free(render);
            LibMpv.mpv_terminate_destroy(mpv);
        }
    }

    private static void Command(nint mpv, params string[] args)
    {
        var ptrs = new nint[args.Length + 1];
        try
        {
            for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
            fixed (nint* p = ptrs) LibMpv.mpv_command(mpv, (byte**)p);
        }
        finally { foreach (var p in ptrs) if (p != 0) Marshal.FreeCoTaskMem(p); }
    }

    /// <summary>Pump events until <paramref name="wanted"/> arrives; false on timeout, cancel, or file error.</summary>
    private static bool WaitFor(nint mpv, MpvEventId wanted, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            MpvEvent* ev = LibMpv.mpv_wait_event(mpv, 0.05);
            if (ev == null || ev->EventId == MpvEventId.None) continue;
            if (ev->EventId == wanted) return true;
            if (ev->EventId == MpvEventId.EndFile)
            {
                var e = (MpvEventEndFile*)ev->Data;
                if (e->Reason == MpvEndFileReason.Error) return false;
            }
            if (ev->EventId == MpvEventId.Shutdown) return false;
        }
        return false;
    }
}
