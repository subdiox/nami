using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nami.Interop;
using Nami.Mpv;
using Nami.Player;

namespace Nami.Controls;

/// <summary>
/// Hosts the mpv video output. Creates the player once the panel has a size,
/// keeps the composition swapchain bound to the SwapChainPanel, and forwards
/// size / DPI changes to mpv.
/// </summary>
public sealed partial class VideoView : SwapChainPanel
{
    private MpvPlayer? _player;
    private nint _swapChain;
    private readonly Queue<Action<MpvPlayer>> _pending = new();

    public MpvPlayer? Player => _player;

    /// <summary>Set by MainPage before the control loads.</summary>
    public PlayerViewModel? Vm { get; set; }

    /// <summary>Raised on the UI thread once the mpv core exists.</summary>
    public event Action<MpvPlayer>? PlayerCreated;

    // Live-resize handling (see ApplyTransform / RequestSize):
    //  - the swapchain transform is updated on every layout change so the frame already on screen
    //    is stretched to the new panel size instead of leaving bands / jumping;
    //  - buffer resizes (mpv: d3d11-composition-size → ResizeBuffers + redraw) are throttled, the
    //    last requested size always wins;
    //  - after a request, the transform is re-evaluated each tick until the buffer matches.
    private static readonly TimeSpan ResizeThrottle = TimeSpan.FromMilliseconds(40);
    private readonly DispatcherQueueTimer _throttle;
    private readonly DispatcherQueueTimer _sync;
    private (int w, int h) _wanted;
    private bool _sizeDirty;
    private DateTime _syncDeadline;

    public VideoView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => { ApplyTransform(); RequestSize(); };
        CompositionScaleChanged += (_, _) => { ApplyTransform(); RequestSize(); };

        _throttle = DispatcherQueue.CreateTimer();
        _throttle.Interval = ResizeThrottle;
        _throttle.IsRepeating = false;
        _throttle.Tick += (_, _) => { if (_sizeDirty) { _sizeDirty = false; PushSize(); _throttle.Start(); } };

        _sync = DispatcherQueue.CreateTimer();
        _sync.Interval = TimeSpan.FromMilliseconds(8);
        _sync.IsRepeating = true;
        _sync.Tick += (_, _) =>
        {
            ApplyTransform();
            var (bw, bh) = SwapChainPanelInterop.GetBufferSize(_swapChain);
            if ((bw == _wanted.w && bh == _wanted.h) || DateTime.UtcNow > _syncDeadline) _sync.Stop();
        };
    }

    /// <summary>Run <paramref name="action"/> now if the player exists, otherwise as soon as it does.</summary>
    public void WhenReady(Action<MpvPlayer> action)
    {
        if (_player is not null) action(_player);
        else _pending.Enqueue(action);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_player is not null) return;
        if (ActualWidth <= 0 || ActualHeight <= 0)
        {
            // Layout hasn't happened yet; SizeChanged will fire and we retry there.
            SizeChanged += CreateOnFirstSize;
            return;
        }
        CreatePlayer();
    }

    private void CreateOnFirstSize(object sender, SizeChangedEventArgs e)
    {
        if (_player is not null || ActualWidth <= 0 || ActualHeight <= 0) return;
        SizeChanged -= CreateOnFirstSize;
        CreatePlayer();
    }

    private void CreatePlayer()
    {
        var (w, h) = PixelSize();
        string configDir = Nami.Services.AppSettings.MpvConfigDirectory;
        Directory.CreateDirectory(configDir);

        App.Log($"T+{Program.Uptime} ms creating mpv core ({w}x{h})");
        if (Vm is null) throw new InvalidOperationException("VideoView.Vm must be set before the control loads");
        var player = new MpvPlayer(DispatcherQueue.GetForCurrentThread(), w, h, configDir, Vm.Services.Settings, Vm.Services.Launch.Extra);
        App.Log($"T+{Program.Uptime} ms mpv core ready");
        player.SwapChainChanged += OnSwapChainChanged;
        _player = player;

        // In case the VO was created before we subscribed (force-window=immediate).
        long? existing = player.GetInt64("display-swapchain");
        if (existing is > 0) OnSwapChainChanged((nint)existing.Value);

        while (_pending.Count > 0) _pending.Dequeue()(player);
        Vm?.Attach(player);
        PlayerCreated?.Invoke(player);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Window is going away. Detach first so XAML never presents a dead swapchain.
        if (_swapChain != 0)
        {
            SwapChainPanelInterop.SetSwapChain(this, 0);
            _swapChain = 0;
        }
        _player?.Dispose();
        _player = null;
    }

    private void OnSwapChainChanged(nint swapChain)
    {
        if (swapChain == _swapChain) return;
        _swapChain = swapChain;
        SwapChainPanelInterop.SetSwapChain(this, swapChain);
        ApplyTransform();
    }

    private (int w, int h) PixelSize()
    {
        int w = (int)Math.Round(ActualWidth * CompositionScaleX);
        int h = (int)Math.Round(ActualHeight * CompositionScaleY);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Ask mpv for the current panel size, at most once per throttle window (last size wins).</summary>
    private void RequestSize()
    {
        if (_player is null) return;
        _wanted = PixelSize();
        if (_throttle.IsRunning) { _sizeDirty = true; return; }
        PushSize();
        _throttle.Start();
    }

    private void PushSize()
    {
        if (_player is null) return;
        _wanted = PixelSize();
        _player.SetOutputSize(_wanted.w, _wanted.h);
        _syncDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (!_sync.IsRunning) _sync.Start();
    }

    /// <summary>Map the buffer onto the panel (DPI inverse once sizes match, a stretch while they do not).</summary>
    private void ApplyTransform()
    {
        if (_swapChain == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;
        var (bw, bh) = SwapChainPanelInterop.GetBufferSize(_swapChain);
        if (bw <= 0 || bh <= 0) { SwapChainPanelInterop.SetTransform(_swapChain, 1f / CompositionScaleX, 1f / CompositionScaleY); return; }
        SwapChainPanelInterop.SetTransform(_swapChain, (float)(ActualWidth / bw), (float)(ActualHeight / bh));
    }
}
