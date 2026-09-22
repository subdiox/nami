using Windows.Win32;

namespace Nami.Interop;

/// <summary>Stateless Win32 helpers for the player window.</summary>
internal static class WindowInterop
{
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

    /// <summary>Diagnostic: what the OS currently shows as the cursor.</summary>
    public static unsafe string CursorState()
    {
        var info = new Windows.Win32.UI.WindowsAndMessaging.CURSORINFO { cbSize = (uint)sizeof(Windows.Win32.UI.WindowsAndMessaging.CURSORINFO) };
        PInvoke.GetCursorInfo(ref info);
        return $"os flags {(uint)info.flags}, hcursor {(nint)info.hCursor:X}";
    }
}
