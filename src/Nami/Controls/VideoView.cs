using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nami.Interop;
using Nami.Mpv;
using Nami.Player;

namespace Nami.Controls;

/// <summary>
/// Hosts the mpv video output. Creates the player once the panel has a size, keeps the composition
/// swapchain bound to the SwapChainPanel, and forwards size / DPI changes to mpv.
///
/// Resizing without flicker: mpv resizes its swapchain and presents a new frame asynchronously, so
/// the panel must not change size before that frame exists. The panel therefore keeps the size the
/// buffer really has ("committed") and is scaled to the host with a XAML render transform, which the
/// XAML compositor applies in the same frame as the layout. When mpv has caught up (buffer matches
/// the target and a frame has had time to present) the panel is committed to the new size and the
/// transform reset. During an interactive resize (WM_ENTERSIZEMOVE … WM_EXITSIZEMOVE) mpv is not
/// asked to resize at all; the last frame keeps playing scaled, and the real resize happens once at
/// the end.
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

    private FrameworkElement? _host;
    private readonly CompositeTransform _transform = new();
    private Windows.Foundation.Size _committed;     // DIPs the buffer currently matches
    private Windows.Foundation.Size _target;        // DIPs the host wants
    private (int w, int h) _wanted;                 // pixels requested from mpv
    private bool _live;                             // inside WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE
    private bool _fillOutput;
    private readonly DispatcherQueueTimer _sync;    // polls the buffer size after a request
    private readonly DispatcherQueueTimer _commit;  // one frame of grace after the buffer matched
    private DateTime _syncDeadline;

    public VideoView()
    {
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        RenderTransform = _transform;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        _commit = DispatcherQueue.CreateTimer();
        _commit.Interval = TimeSpan.FromMilliseconds(40);
        _commit.IsRepeating = false;

        _sync = DispatcherQueue.CreateTimer();
        _sync.Interval = TimeSpan.FromMilliseconds(8);
        _sync.IsRepeating = true;
        _sync.Tick += (_, _) =>
        {
            var (bw, bh) = SwapChainPanelInterop.GetBufferSize(_swapChain);
            bool matched = bw == _wanted.w && bh == _wanted.h;
            if (matched || DateTime.UtcNow > _syncDeadline)
            {
                _sync.Stop();
                _commit.Start();   // give mpv one frame to present into the resized buffer
            }
        };

        _commit.Tick += (_, _) =>
        {
            double dpi = Dpi;
            _committed = new Windows.Foundation.Size(_wanted.w / dpi, _wanted.h / dpi);
            ApplyLayout();
        };
    }

    /// <summary>Run <paramref name="action"/> now if the player exists, otherwise as soon as it does.</summary>
    public void WhenReady(Action<MpvPlayer> action)
    {
        if (_player is not null) action(_player);
        else _pending.Enqueue(action);
    }

    /// <summary>Called by the window on WM_ENTERSIZEMOVE / WM_EXITSIZEMOVE.</summary>
    public void SetLiveResize(bool live)
    {
        if (_live == live) return;
        _live = live;
        if (!live && _target != _committed) PushSize();
    }

    private double Dpi => XamlRoot?.RasterizationScale ?? 1.0;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _host = Parent as FrameworkElement;
        if (_host is not null) _host.SizeChanged += OnHostSizeChanged;
        if (XamlRoot is not null) XamlRoot.Changed += OnXamlRootChanged;
        if (_host is { ActualWidth: > 0, ActualHeight: > 0 })
        {
            _target = _committed = new Windows.Foundation.Size(_host.ActualWidth, _host.ActualHeight);
            ApplyLayout();
            if (_player is null) CreatePlayer();
        }
    }

    private void OnHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _target = e.NewSize;
        if (_target.Width <= 0 || _target.Height <= 0) return;
        if (_player is null)
        {
            _committed = _target;
            ApplyLayout();
            CreatePlayer();
            return;
        }
        ApplyLayout();
        if (!_live) PushSize();
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        // DPI change: the buffer must be re-created at the new pixel size.
        ApplyDpiTransform();
        if (_player is not null && !_live) PushSize();
    }

    /// <summary>
    /// Panel at its committed size, scaled uniformly to fit the host and centered; identity once the
    /// buffer matches the host.
    /// </summary>
    private void ApplyLayout()
    {
        if (_committed.Width <= 0 || _committed.Height <= 0) return;
        bool same = Math.Abs(_committed.Width - _target.Width) < 0.5 && Math.Abs(_committed.Height - _target.Height) < 0.5;
        Width = _committed.Width;
        Height = _committed.Height;
        if (same)
        {
            _transform.ScaleX = _transform.ScaleY = 1;
            _transform.TranslateX = _transform.TranslateY = 0;
            return;
        }
        double s = Math.Min(_target.Width / _committed.Width, _target.Height / _committed.Height);
        _transform.ScaleX = _transform.ScaleY = s;
        _transform.TranslateX = (_target.Width - _committed.Width * s) / 2;
        _transform.TranslateY = (_target.Height - _committed.Height * s) / 2;
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
        _wanted = (w, h);

        // In case the VO was created before we subscribed (force-window=immediate).
        long? existing = player.GetInt64("display-swapchain");
        if (existing is > 0) OnSwapChainChanged((nint)existing.Value);

        while (_pending.Count > 0) _pending.Dequeue()(player);
        Vm.Attach(player);
        Vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(PlayerViewModel.VideoSize)) { _fillOutput = false; ApplyKeepAspect(); } };
        ApplyKeepAspect();
        PlayerCreated?.Invoke(player);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _sync.Stop();
        _commit.Stop();
        if (_host is not null) _host.SizeChanged -= OnHostSizeChanged;
        if (XamlRoot is not null) XamlRoot.Changed -= OnXamlRootChanged;
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
        ApplyDpiTransform();
    }

    /// <summary>Pixel size the host wants (DIPs × DPI).</summary>
    private (int w, int h) PixelSize()
    {
        double dpi = Dpi;
        int w = (int)Math.Round(_target.Width * dpi);
        int h = (int)Math.Round(_target.Height * dpi);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>Ask mpv for the host size and start watching for the buffer to catch up.</summary>
    private void PushSize()
    {
        if (_player is null || _target.Width <= 0 || _target.Height <= 0) return;
        var wanted = PixelSize();
        if (wanted == _wanted && _committed == _target) return;
        _wanted = wanted;
        ApplyKeepAspect();
        _player.SetOutputSize(_wanted.w, _wanted.h);
        _commit.Stop();
        _syncDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(1);
        if (!_sync.IsRunning) _sync.Start();
    }

    /// <summary>
    /// SwapChainPanel presents the buffer at its pixel size in DIPs; map one buffer pixel to one
    /// screen pixel. (The live-resize scaling is a XAML transform on the panel, not this matrix.)
    /// </summary>
    private void ApplyDpiTransform()
    {
        if (_swapChain == 0) return;
        float inv = (float)(1.0 / Dpi);
        SwapChainPanelInterop.SetTransform(_swapChain, inv, inv);
    }

    /// <summary>
    /// mpv letterboxes whenever the output is not exactly the video aspect, and the window's aspect
    /// lock can only be integer-exact, so a 1 px black line would show up at many sizes. When the
    /// output is within 1 % of the video aspect, let mpv fill it instead (a sub-pixel stretch nobody
    /// can see); at real letterbox aspects (maximized, snapped, full screen) keep the bars.
    /// </summary>
    private void ApplyKeepAspect()
    {
        if (_player is null || Vm is null) return;
        bool fill = false;
        if (Vm.VideoSize.IsValid && _wanted.w > 0 && _wanted.h > 0)
            fill = Math.Abs((double)_wanted.w / _wanted.h / Vm.VideoSize.Aspect - 1) < 0.01;
        if (fill == _fillOutput) return;
        _fillOutput = fill;
        try { _player.SetProperty("keepaspect", !fill); } catch (MpvException) { }
    }
}
