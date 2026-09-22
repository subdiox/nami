using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>
/// Window subclass that completes the "video area is the caption" design. The caption hit-test
/// (set through InputNonClientPointerSource) gives us Windows' own drag, Aero Snap preview and
/// snap groups; this hook takes back what the caption would otherwise do differently:
/// double-click → full screen (not maximize), right-click → our menu (not the system menu),
/// middle-click → mpv, the maximize button → full screen, and a hidden cursor over the caption.
/// </summary>
internal sealed unsafe class NonClientHook : IDisposable
{
    private const nuint SubclassId = 0x4E43;

    public sealed class Callbacks
    {
        public required Action OnMaximizeClick { get; init; }
        /// <summary>Screen point inside the video (not the title strip)?</summary>
        public required Func<int, int, bool> IsVideoArea { get; init; }
        public required Action OnVideoDoubleClick { get; init; }
        public required Action<int, int> OnVideoRightClick { get; init; }
        public required Action OnVideoMiddleClick { get; init; }
        public required Func<bool> IsCursorHidden { get; init; }
        /// <summary>Full screen: the client area is the whole window (no frame at all).</summary>
        public required Func<bool> IsFullScreen { get; init; }
        /// <summary>WM_ENTERSIZEMOVE (true) / WM_EXITSIZEMOVE (false).</summary>
        public required Action<bool> OnSizeMove { get; init; }
        /// <summary>Files dropped from Explorer onto the window (the caption area has no XAML drop target).</summary>
        public required Action<List<string>> OnFilesDropped { get; init; }
    }

    private readonly HWND _hwnd;
    private readonly Callbacks _cb;
    private GCHandle _self;
    private bool _installed;

    public NonClientHook(nint hwnd, Callbacks callbacks)
    {
        _hwnd = (HWND)hwnd;
        _cb = callbacks;
        _self = GCHandle.Alloc(this, GCHandleType.Weak);
        _installed = PInvoke.SetWindowSubclass(_hwnd, &Proc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
        PInvoke.DragAcceptFiles(_hwnd, true);
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
        if (GCHandle.FromIntPtr((nint)refData).Target is not NonClientHook self)
            return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
        var cb = self._cb;
        nuint hit = (nuint)wParam;
        int x = (short)(ushort)((nuint)lParam.Value & 0xFFFF);
        int y = (short)(ushort)(((nuint)lParam.Value >> 16) & 0xFFFF);

        switch (msg)
        {
            case PInvoke.WM_NCLBUTTONDOWN or PInvoke.WM_NCLBUTTONUP or PInvoke.WM_NCLBUTTONDBLCLK when hit == PInvoke.HTMAXBUTTON:
                if (msg == PInvoke.WM_NCLBUTTONUP) cb.OnMaximizeClick();
                return (LRESULT)0;                                   // no system maximize

            case PInvoke.WM_NCLBUTTONDBLCLK when hit == PInvoke.HTCAPTION && cb.IsVideoArea(x, y):
                cb.OnVideoDoubleClick();
                return (LRESULT)0;                                   // no maximize on the video

            case PInvoke.WM_NCRBUTTONDOWN when hit == PInvoke.HTCAPTION && cb.IsVideoArea(x, y):
                return (LRESULT)0;
            case PInvoke.WM_NCRBUTTONUP when hit == PInvoke.HTCAPTION && cb.IsVideoArea(x, y):
                cb.OnVideoRightClick(x, y);
                return (LRESULT)0;                                   // no system menu

            case PInvoke.WM_NCMBUTTONDOWN when hit == PInvoke.HTCAPTION && cb.IsVideoArea(x, y):
                cb.OnVideoMiddleClick();
                return (LRESULT)0;

            case PInvoke.WM_SETCURSOR when ((nuint)lParam.Value & 0xFFFF) == PInvoke.HTCAPTION && cb.IsCursorHidden():
                PInvoke.SetCursor(default);
                return (LRESULT)1;

            case PInvoke.WM_DROPFILES:
            {
                var drop = (Windows.Win32.UI.Shell.HDROP)(nint)wParam.Value;
                uint count = PInvoke.DragQueryFile(drop, 0xFFFFFFFF, null, 0);
                var files = new List<string>((int)count);
                char* buf = stackalloc char[1024];
                for (uint i = 0; i < count; i++)
                {
                    uint n = PInvoke.DragQueryFile(drop, i, buf, 1024);
                    if (n > 0) files.Add(new string(buf, 0, (int)n));
                }
                PInvoke.DragFinish(drop);
                if (files.Count > 0) cb.OnFilesDropped(files);
                return (LRESULT)0;
            }

            case PInvoke.WM_NCCALCSIZE when wParam != 0 && cb.IsFullScreen():
                // The overlapped presenter keeps WS_DLGFRAME even without border and title bar;
                // claim the frame for the client so the video reaches the screen edges.
                return (LRESULT)0;
            case PInvoke.WM_ENTERSIZEMOVE:
                cb.OnSizeMove(true);
                break;
            case PInvoke.WM_EXITSIZEMOVE:
                cb.OnSizeMove(false);
                break;
        }
        return PInvoke.DefSubclassProc(hwnd, msg, wParam, lParam);
    }
}
