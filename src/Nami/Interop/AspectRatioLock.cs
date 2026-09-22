using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>
/// Keeps a window's client area at a fixed aspect ratio while the user drags its edges,
/// the way IINA constrains its window to the video. One instance per window; the
/// WM_SIZING handler is a window subclass that finds its instance through a GCHandle.
/// </summary>
internal sealed unsafe class AspectRatioLock : IDisposable
{
    private const nuint SubclassId = 0x4E41;

    private readonly HWND _hwnd;
    private GCHandle _self;
    private bool _installed;

    /// <summary>Client aspect (w/h); 0 disables the constraint.</summary>
    public double Aspect
    {
        get;
        set => field = value > 0 && double.IsFinite(value) ? value : 0;
    }

    public AspectRatioLock(nint hwnd)
    {
        _hwnd = (HWND)hwnd;
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
        if (msg == PInvoke.WM_SIZING
            && GCHandle.FromIntPtr((nint)refData).Target is AspectRatioLock { Aspect: > 0 } self)
        {
            double aspect = self.Aspect;
            var rect = (RECT*)(nint)lParam;
            RECT win, client;
            if (PInvoke.GetWindowRect(hwnd, &win) && PInvoke.GetClientRect(hwnd, &client))
            {
                int ncW = (win.right - win.left) - client.right;
                int ncH = (win.bottom - win.top) - client.bottom;
                int proposedW = rect->right - rect->left - ncW;
                int proposedH = rect->bottom - rect->top - ncH;
                uint edge = (uint)wParam;

                int w, h;
                if (edge is PInvoke.WMSZ_TOP or PInvoke.WMSZ_BOTTOM)
                {
                    h = proposedH;
                    w = (int)Math.Round(h * aspect);
                }
                else if (edge is PInvoke.WMSZ_LEFT or PInvoke.WMSZ_RIGHT)
                {
                    w = proposedW;
                    h = (int)Math.Round(w / aspect);
                }
                else if (Math.Abs(proposedH - client.bottom) > Math.Abs(proposedW - client.right))
                {
                    h = proposedH;
                    w = (int)Math.Round(h * aspect);
                }
                else
                {
                    w = proposedW;
                    h = (int)Math.Round(w / aspect);
                }

                int totalW = w + ncW, totalH = h + ncH;
                switch (edge)
                {
                    case PInvoke.WMSZ_LEFT:
                    case PInvoke.WMSZ_BOTTOMLEFT:
                        rect->left = rect->right - totalW;
                        rect->bottom = rect->top + totalH;
                        break;
                    case PInvoke.WMSZ_TOPLEFT:
                        rect->left = rect->right - totalW;
                        rect->top = rect->bottom - totalH;
                        break;
                    case PInvoke.WMSZ_TOP:
                    case PInvoke.WMSZ_TOPRIGHT:
                        rect->top = rect->bottom - totalH;
                        rect->right = rect->left + totalW;
                        break;
                    default:
                        rect->right = rect->left + totalW;
                        rect->bottom = rect->top + totalH;
                        break;
                }
                return (LRESULT)1;
            }
        }
        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
