using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>
/// Lets the XAML "maximize" caption button be the system maximize button (so Windows 11 shows the
/// Snap Layouts flyout on hover) while the click itself is ours: the non-client button messages
/// for HTMAXBUTTON are consumed here instead of reaching DefWindowProc, which would maximize.
/// </summary>
internal sealed unsafe class CaptionButtonHook : IDisposable
{
    private const nuint SubclassId = 0x4E43;

    private readonly HWND _hwnd;
    private readonly Action _onMaximizeClick;
    private GCHandle _self;
    private bool _installed;

    public CaptionButtonHook(nint hwnd, Action onMaximizeClick)
    {
        _hwnd = (HWND)hwnd;
        _onMaximizeClick = onMaximizeClick;
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _installed = PInvoke.SetWindowSubclass(_hwnd, &Proc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    public void Dispose()
    {
        if (_installed) PInvoke.RemoveWindowSubclass(_hwnd, &Proc, SubclassId);
        _installed = false;
        if (_self.IsAllocated) _self.Free();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        if ((msg is PInvoke.WM_NCLBUTTONDOWN or PInvoke.WM_NCLBUTTONUP or PInvoke.WM_NCLBUTTONDBLCLK)
            && (nuint)wParam == PInvoke.HTMAXBUTTON
            && GCHandle.FromIntPtr((nint)refData).Target is CaptionButtonHook self)
        {
            if (msg == PInvoke.WM_NCLBUTTONUP) self._onMaximizeClick();
            return (LRESULT)0;   // swallow: no system maximize
        }
        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
