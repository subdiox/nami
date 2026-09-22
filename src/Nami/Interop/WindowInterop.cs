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
}
