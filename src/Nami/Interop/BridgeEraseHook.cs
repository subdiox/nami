using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nami.Interop;

/// <summary>
/// WinUI's content island lags the window frame by a frame during a live resize; the strip DWM
/// shows meanwhile is the island window's erased background (black). This subclass paints that
/// erase with a stretched snapshot of the video instead, so the strip continues the picture and
/// the lag stops reading as flicker. Without a snapshot it paints black.
/// </summary>
internal sealed unsafe class BridgeEraseHook : IDisposable
{
    private const nuint SubclassId = 0x4E44;
    private readonly HWND _bridge;
    private readonly HWND _top;
    private GCHandle _self;
    private bool _installed;

    private byte[]? _pixels;         // bgr0, top-down
    private GCHandle _pixelsPin;
    private int _w, _h, _stride;

    public BridgeEraseHook(nint topLevel)
    {
        _top = (HWND)topLevel;
        _bridge = FindChild(_top, "Microsoft.UI.Content.DesktopChildSiteBridge");
        if (_bridge == default) { App.Log("erase hook: bridge window not found"); return; }
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _installed = PInvoke.SetWindowSubclass(_bridge, &Proc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
    }

    /// <summary>
    /// Keep the island over the whole client area. WinUI places it 1 px below the top edge of a
    /// non-maximized window (room for the top resize border), which in full screen would leave a
    /// 1 px line of the window background on the top edge of the screen.
    /// </summary>
    public bool FillParent
    {
        get;
        set
        {
            field = value;
            if (value && _bridge != default)
                PInvoke.SetWindowPos(_bridge, HWND.Null, 0, 0, 0, 0,
                    SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);   // adjusted by the hook
        }
    }

    /// <summary>Use this frame (bgr0 rows, top-down) for erases; null goes back to black.</summary>
    public void SetSnapshot(byte[]? pixels, int width, int height, int stride)
    {
        if (_pixelsPin.IsAllocated) _pixelsPin.Free();
        _pixels = pixels;
        _w = width; _h = height; _stride = stride;
        if (pixels is not null) _pixelsPin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
    }

    public void Dispose()
    {
        if (_installed) PInvoke.RemoveWindowSubclass(_bridge, &Proc, SubclassId);
        _installed = false;
        SetSnapshot(null, 0, 0, 0);
        if (_self.IsAllocated) _self.Free();
    }

    [ThreadStatic] private static HWND t_found;
    [ThreadStatic] private static string? t_wanted;

    private static HWND FindChild(HWND top, string className)
    {
        t_found = default; t_wanted = className;
        PInvoke.EnumChildWindows(top, &EnumProc, 0);
        return t_found;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumProc(HWND h, LPARAM _)
    {
        char* buf = stackalloc char[128];
        int n = PInvoke.GetClassName(h, buf, 128);
        if (n > 0 && new string(buf, 0, n) == t_wanted) { t_found = h; return false; }
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT Proc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam, nuint id, nuint refData)
    {
        if (msg == PInvoke.WM_WINDOWPOSCHANGING && GCHandle.FromIntPtr((nint)refData).Target is BridgeEraseHook { FillParent: true } fill)
        {
            var wp = (WINDOWPOS*)(nint)lParam.Value;
            RECT client;
            if (PInvoke.GetClientRect(fill._top, &client))
            {
                wp->x = 0; wp->y = 0; wp->cx = client.right; wp->cy = client.bottom;
                wp->flags &= ~(SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE);
            }
            return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
        }
        if (msg == PInvoke.WM_ERASEBKGND && GCHandle.FromIntPtr((nint)refData).Target is BridgeEraseHook self)
        {
            var hdc = (HDC)(nint)wParam.Value;
            RECT rc;
            if (!PInvoke.GetClientRect(hwnd, &rc)) return (LRESULT)1;
            if (self._pixels is not null && self._w > 0 && self._h > 0)
            {
                var bmi = new BITMAPINFO();
                bmi.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
                bmi.bmiHeader.biWidth = self._stride / 4;
                bmi.bmiHeader.biHeight = -self._h;          // top-down
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = (uint)BI_COMPRESSION.BI_RGB;
                PInvoke.SetStretchBltMode(hdc, STRETCH_BLT_MODE.COLORONCOLOR);
                PInvoke.StretchDIBits(hdc, 0, 0, rc.right - rc.left, rc.bottom - rc.top, 0, 0, self._w, self._h,
                    (void*)self._pixelsPin.AddrOfPinnedObject(), &bmi, DIB_USAGE.DIB_RGB_COLORS, ROP_CODE.SRCCOPY);
            }
            else
            {
                var brush = PInvoke.CreateSolidBrush(new COLORREF(0));
                PInvoke.FillRect(hdc, &rc, brush);
                PInvoke.DeleteObject(brush);
            }
            return (LRESULT)1;
        }
        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
