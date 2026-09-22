using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

internal static class WindowInterop
{
    private static bool _cursorHidden;

    /// <summary>Hide the mouse cursor for this thread's windows (idempotent).</summary>
    public static void HideCursor()
    {
        if (_cursorHidden) return;
        _cursorHidden = true;
        PInvoke.ShowCursor(false);
    }

    public static void ShowCursor()
    {
        if (!_cursorHidden) return;
        _cursorHidden = false;
        PInvoke.ShowCursor(true);
    }

    /// <summary>Let the user drag the window by its client area, as if they grabbed the caption.</summary>
    public static void BeginWindowDrag(nint hwnd)
    {
        PInvoke.ReleaseCapture();
        PInvoke.SendMessage((HWND)hwnd, PInvoke.WM_NCLBUTTONDOWN, new WPARAM(PInvoke.HTCAPTION), new LPARAM(0));
    }

    public static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime());
}
