using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>
/// Hides the mouse cursor while it is over the player window: ShowCursor's per-thread counter,
/// an immediate SetCursor(null), and a WM_SETCURSOR subclass on WinUI's input window (one per
/// player window) that answers "no cursor" while hidden.
/// </summary>
internal sealed unsafe class CursorHider : IDisposable
{
    private const nuint SubclassId = 0x4E42;

    private readonly HWND _site;
    private GCHandle _self;
    private bool _installed;

    public bool Hidden { get; private set; }

    public CursorHider(nint topLevelHwnd)
    {
        _site = FindInputSite((HWND)topLevelHwnd);
        if (_site == default) { App.Log("cursor: input site window not found"); return; }
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _installed = PInvoke.SetWindowSubclass(_site, &Proc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    public void Hide()
    {
        if (Hidden) return;
        Hidden = true;
        // Belt and braces: the per-thread display counter survives XAML's own SetCursor calls,
        // SetCursor(null) takes effect without waiting for a mouse move, and the WM_SETCURSOR
        // subclass keeps it hidden on later cursor updates.
        PInvoke.ShowCursor(false);
        PInvoke.SetCursor(default);
    }

    public void Show()
    {
        if (!Hidden) return;
        Hidden = false;
        PInvoke.ShowCursor(true);
        PInvoke.SetCursor(PInvoke.LoadCursor(default, PInvoke.IDC_ARROW));
    }

    public void Dispose()
    {
        Show();
        if (_installed) PInvoke.RemoveWindowSubclass(_site, &Proc, SubclassId);
        _installed = false;
        if (_self.IsAllocated) _self.Free();
    }

    [ThreadStatic] private static HWND t_found;

    private static HWND FindInputSite(HWND top)
    {
        t_found = default;
        PInvoke.EnumChildWindows(top, &EnumProc, 0);
        return t_found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumProc(HWND h, LPARAM _)
    {
        char* buf = stackalloc char[64];
        int n = PInvoke.GetClassName(h, buf, 64);
        if (n > 0 && new string(buf, 0, n) == "InputSiteWindowClass") { t_found = h; return false; }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        if (msg == PInvoke.WM_SETCURSOR
            && GCHandle.FromIntPtr((nint)refData).Target is CursorHider { Hidden: true })
        {
            PInvoke.SetCursor(default);
            return (LRESULT)1;
        }
        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
