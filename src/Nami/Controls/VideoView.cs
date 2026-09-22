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

    public VideoView()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => PushSize();
        CompositionScaleChanged += (_, _) => { PushSize(); ApplyScale(); };
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
        var player = new MpvPlayer(DispatcherQueue.GetForCurrentThread(), w, h, configDir);
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
        ApplyScale();
    }

    private (int w, int h) PixelSize()
    {
        int w = (int)Math.Round(ActualWidth * CompositionScaleX);
        int h = (int)Math.Round(ActualHeight * CompositionScaleY);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    private void PushSize()
    {
        if (_player is null) return;
        var (w, h) = PixelSize();
        _player.SetOutputSize(w, h);
    }

    private void ApplyScale()
    {
        if (_swapChain == 0) return;
        SwapChainPanelInterop.SetCompositionScale(_swapChain, CompositionScaleX, CompositionScaleY);
    }
}
