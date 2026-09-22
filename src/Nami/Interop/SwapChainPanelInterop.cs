using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Controls;
using Windows.Win32;
using Windows.Win32.Graphics.Dxgi;

namespace Nami.Interop;

/// <summary>
/// Binds a DXGI composition swapchain (owned by mpv) to a WinUI 3 SwapChainPanel.
/// </summary>
internal static unsafe class SwapChainPanelInterop
{
    // WinUI 3 (Microsoft.UI.Xaml) ISwapChainPanelNative, from microsoft.ui.xaml.media.dxinterop.h.
    private static readonly Guid IID_ISwapChainPanelNative = new("63aad0b8-7c24-40ff-85a8-640d944cc325");

    /// <summary>Call on the UI thread. Pass 0 to detach.</summary>
    public static void SetSwapChain(SwapChainPanel panel, nint swapChain)
    {
        nint unknown = ((WinRT.IWinRTObject)panel).NativeObject.ThisPtr; // borrowed
        Guid iid = IID_ISwapChainPanelNative;
        int hr = Marshal.QueryInterface(unknown, in iid, out nint native);
        Marshal.ThrowExceptionForHR(hr);
        try
        {
            // vtable: IUnknown[0..2], ISwapChainPanelNative::SetSwapChain = slot 3
            var vtbl = *(void***)native;
            var setSwapChain = (delegate* unmanaged[MemberFunction]<nint, nint, int>)vtbl[3];
            Marshal.ThrowExceptionForHR(setSwapChain(native, swapChain));
        }
        finally
        {
            Marshal.Release(native);
        }
    }

    /// <summary>
    /// SwapChainPanel presents the swapchain in DIPs at the buffer's pixel size unless a transform
    /// is set. This maps the buffer onto the panel: (panel DIPs / buffer pixels) per axis, which is
    /// the plain DPI inverse once the buffer matches the panel, and a temporary stretch while it
    /// does not (during a live resize, until mpv has resized and presented).
    /// </summary>
    public static void SetTransform(nint swapChain, float m11, float m22, float dx = 0, float dy = 0)
    {
        if (swapChain == 0 || !(m11 > 0) || !(m22 > 0)) return;
        Guid iid = IDXGISwapChain2.IID_Guid;
        int hr = Marshal.QueryInterface(swapChain, in iid, out nint p);
        if (hr < 0) return; // not a DXGI 1.3 swapchain; nothing we can do
        try
        {
            var sc = (IDXGISwapChain2*)p;
            var m = new DXGI_MATRIX_3X2_F { _11 = m11, _22 = m22, _31 = dx, _32 = dy };
            sc->SetMatrixTransform(&m);
        }
        finally
        {
            Marshal.Release(p);
        }
    }

    /// <summary>Current back-buffer size in pixels (0,0 if unknown).</summary>
    public static (int w, int h) GetBufferSize(nint swapChain)
    {
        if (swapChain == 0) return (0, 0);
        Guid iid = IDXGISwapChain2.IID_Guid;
        int hr = Marshal.QueryInterface(swapChain, in iid, out nint p);
        if (hr < 0) return (0, 0);
        try
        {
            var sc = (IDXGISwapChain2*)p;
            var desc = sc->GetDesc1();
            return ((int)desc.Width, (int)desc.Height);
        }
        catch { return (0, 0); }
        finally
        {
            Marshal.Release(p);
        }
    }
}
