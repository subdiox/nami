using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami;

public static class Program
{
    // NAMI_INSTANCE=<name> runs a separate instance (own single-instance key); for development.
    private static readonly string InstanceKey =
        Environment.GetEnvironmentVariable("NAMI_INSTANCE") is { Length: > 0 } k ? "nami-" + k : "nami-main";

    /// <summary>Milliseconds since the process started (for startup timing logs).</summary>
    public static long Uptime => (long)(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // Single instance: hand our activation (command line) to the running instance and exit.
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!main.IsCurrent)
        {
            if (RedirectActivation(main, activation)) return 0;
            // The registered instance did not answer (it probably crashed): take over instead of
            // hanging around as an invisible process that would absorb every later launch.
            try { main.UnregisterKey(); } catch { }
            main = AppInstance.FindOrRegisterForKey(InstanceKey);
            if (!main.IsCurrent) return 0;
        }

        try
        {
            Application.Start(p =>
            {
                var ctx = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(ctx);
                new App();
            });
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(App.LogPath, "FATAL: " + ex + Environment.NewLine); } catch { }
            throw;
        }
        return 0;
    }

    /// <summary>
    /// RedirectActivationToAsync must be awaited without blocking the STA message pump,
    /// so wait on a Win32 event with CoWaitForMultipleObjects (per the Windows App SDK docs).
    /// </summary>
    private static unsafe bool RedirectActivation(AppInstance target, AppActivationArguments args)
    {
        using var done = PInvoke.CreateEvent((Windows.Win32.Security.SECURITY_ATTRIBUTES?)null, true, false, (string?)null);
        bool ok = false;
        var op = target.RedirectActivationToAsync(args);
        op.Completed = (o, _) => { ok = o.Status == Windows.Foundation.AsyncStatus.Completed; PInvoke.SetEvent(done); };

        HANDLE h = (HANDLE)done.DangerousGetHandle();
        uint index;
        var hr = PInvoke.CoWaitForMultipleObjects(0 /* CWMO_DEFAULT */, 10_000, new ReadOnlySpan<HANDLE>(&h, 1), out index);
        if (hr.Failed || !ok)
        {
            try { File.AppendAllText(App.LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] redirect to running instance failed (hr=0x{(uint)hr.Value:X8}, ok={ok}); starting standalone{Environment.NewLine}"); } catch { }
            return false;
        }
        return true;
    }
}
