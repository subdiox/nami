using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>
/// Keeps the window's client area at a fixed aspect ratio while the user drags its edges,
/// the way IINA constrains its window to the video. Implemented with a window subclass
/// that rewrites the proposed rectangle in WM_SIZING.
/// </summary>
internal static unsafe class AspectRatioLock
{
    private static double s_aspect;          // client width / height; 0 = unlocked
    private static HWND s_hwnd;
    private static bool s_installed;
    private const nuint SubclassId = 0x4E41;

    public static void Install(nint hwnd)
    {
        if (s_installed) return;
        s_hwnd = (HWND)hwnd;
        s_installed = PInvoke.SetWindowSubclass(s_hwnd, &Proc, SubclassId, 0);
    }

    public static void Uninstall()
    {
        if (!s_installed) return;
        PInvoke.RemoveWindowSubclass(s_hwnd, &Proc, SubclassId);
        s_installed = false;
    }

    /// <summary>Set the client aspect (w/h). Pass 0 to stop constraining.</summary>
    public static void SetAspect(double aspect) => s_aspect = aspect > 0 && double.IsFinite(aspect) ? aspect : 0;

    public static double Aspect => s_aspect;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        if (msg == PInvoke.WM_SIZING && s_aspect > 0)
        {
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
                    w = (int)Math.Round(h * s_aspect);
                }
                else if (edge is PInvoke.WMSZ_LEFT or PInvoke.WMSZ_RIGHT)
                {
                    w = proposedW;
                    h = (int)Math.Round(w / s_aspect);
                }
                else
                {
                    // Corner drag: follow whichever axis moved more.
                    if (Math.Abs(proposedH - client.bottom) > Math.Abs(proposedW - client.right))
                    { h = proposedH; w = (int)Math.Round(h * s_aspect); }
                    else
                    { w = proposedW; h = (int)Math.Round(w / s_aspect); }
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
