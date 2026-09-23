using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Nami.Interop;

/// <summary>
/// Tells the shell that a window is (or no longer is) full screen, the way mpv does. The taskbar
/// only hides behind a window it recognises as full screen; its own detection is heuristic and
/// sometimes leaves the taskbar on top of a borderless window that covers the monitor.
/// </summary>
internal static unsafe class TaskbarInterop
{
    private static readonly Guid CLSID_TaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");

    public static void MarkFullscreen(nint hwnd, bool on)
    {
        ITaskbarList2* list = null;
        try
        {
            fixed (Guid* clsid = &CLSID_TaskbarList)
            {
                Guid iid = ITaskbarList2.IID_Guid;
                if (PInvoke.CoCreateInstance(clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, &iid, (void**)&list).Failed) return;
            }
            list->HrInit();
            list->MarkFullscreenWindow((HWND)hwnd, on);
        }
        catch (Exception ex)
        {
            App.Log("taskbar: " + ex.Message);
        }
        finally
        {
            if (list != null) list->Release();
        }
    }
}
