using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Nami.Interop;

/// <summary>Stateless Win32 helpers for the player window.</summary>
internal static class WindowInterop
{
    /// <summary>
    /// Show or hide the frame DWM paints around top-level windows on Windows 11 (1 px border and
    /// rounded corners). Hidden in full screen, where the overlapped presenter's frame styles would
    /// otherwise leave a line on every screen edge and rounded screen corners.
    /// </summary>
    public static unsafe void SetDwmFrame(nint hwnd, bool visible)
    {
        const uint DWMWA_COLOR_DEFAULT = 0xFFFFFFFF, DWMWA_COLOR_NONE = 0xFFFFFFFE;
        uint color = visible ? DWMWA_COLOR_DEFAULT : DWMWA_COLOR_NONE;
        PInvoke.DwmSetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &color, sizeof(uint));
        // Rounded corners would show the desktop in the four corners of the screen.
        var corners = visible ? DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DEFAULT : DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute((HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corners, sizeof(DWM_WINDOW_CORNER_PREFERENCE));
    }

    public static TimeSpan DoubleClickTime => TimeSpan.FromMilliseconds(PInvoke.GetDoubleClickTime());

    /// <summary>Cursor position in physical screen pixels (same space as AppWindow.Position).</summary>
    public static System.Drawing.Point CursorPosition
    {
        get
        {
            PInvoke.GetCursorPos(out var p);
            return p;
        }
    }

    /// <summary>
    /// Make Windows re-evaluate the cursor under the mouse now (it normally does so only on the next
    /// mouse move): a WM_SETCURSOR round-trip to the window under the cursor, if it is ours.
    /// </summary>
    public static unsafe void RefreshCursor()
    {
        PInvoke.GetCursorPos(out var pos);
        var under = PInvoke.WindowFromPoint(pos);
        if (under == default) return;
        PInvoke.GetWindowThreadProcessId(under, out uint pid);
        if (pid != (uint)Environment.ProcessId) return;
        var ht = PInvoke.SendMessage(under, PInvoke.WM_NCHITTEST, default, new LPARAM(((nint)(ushort)(short)pos.Y << 16) | (ushort)(short)pos.X));
        PInvoke.SendMessage(under, PInvoke.WM_SETCURSOR, new WPARAM((nuint)under.Value), new LPARAM(((nint)PInvoke.WM_MOUSEMOVE << 16) | (ushort)(short)ht.Value));
    }

    /// <summary>Diagnostic: what the OS currently shows as the cursor.</summary>
    public static unsafe string CursorState()
    {
        var info = new Windows.Win32.UI.WindowsAndMessaging.CURSORINFO { cbSize = (uint)sizeof(Windows.Win32.UI.WindowsAndMessaging.CURSORINFO) };
        PInvoke.GetCursorInfo(ref info);
        return $"os flags {(uint)info.flags}, hcursor {(nint)info.hCursor:X}";
    }
}
