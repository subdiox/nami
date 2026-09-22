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
    /// SwapChainPanel presents the swapchain in DIPs; the buffer is in physical pixels.
    /// Apply the inverse composition scale so one buffer pixel maps to one screen pixel.
    /// </summary>
    public static void SetCompositionScale(nint swapChain, float scaleX, float scaleY)
    {
        if (swapChain == 0) return;
        Guid iid = IDXGISwapChain2.IID_Guid;
        int hr = Marshal.QueryInterface(swapChain, in iid, out nint p);
        if (hr < 0) return; // not a DXGI 1.3 swapchain; nothing we can do
        try
        {
            var sc = (IDXGISwapChain2*)p;
            var m = new DXGI_MATRIX_3X2_F { _11 = 1f / scaleX, _22 = 1f / scaleY };
            sc->SetMatrixTransform(&m);
        }
        finally
        {
            Marshal.Release(p);
        }
    }
}
