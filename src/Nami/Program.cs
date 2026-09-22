using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami;

public static class Program
{
    private const string InstanceKey = "nami-main";

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
            RedirectActivation(main, activation);
            return 0;
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
    private static unsafe void RedirectActivation(AppInstance target, AppActivationArguments args)
    {
        using var done = PInvoke.CreateEvent((Windows.Win32.Security.SECURITY_ATTRIBUTES?)null, true, false, (string?)null);
        var op = target.RedirectActivationToAsync(args);
        op.Completed = (_, _) => PInvoke.SetEvent(done);

        HANDLE h = (HANDLE)done.DangerousGetHandle();
        uint index;
        PInvoke.CoWaitForMultipleObjects(0 /* CWMO_DEFAULT */, PInvoke.INFINITE, new ReadOnlySpan<HANDLE>(&h, 1), out index);
    }
}
