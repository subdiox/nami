using Windows.Win32;
using Windows.Win32.Foundation;

namespace Nami.Interop;

/// <summary>Stateless Win32 helpers for the player window.</summary>
internal static class WindowInterop
{
    /// <summary>Let the user drag the window by its client area, as if they grabbed the caption.</summary>
    public static void BeginWindowDrag(nint hwnd)
    {
        PInvoke.ReleaseCapture();
        PInvoke.SendMessage((HWND)hwnd, PInvoke.WM_NCLBUTTONDOWN, new WPARAM(PInvoke.HTCAPTION), new LPARAM(0));
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
}

/// <summary>
/// Hides / shows the mouse cursor for the calling UI thread. ShowCursor keeps a counter,
/// so each owner tracks its own state and never unbalances the count.
/// </summary>
internal sealed class CursorVisibility
{
    private bool _hidden;

    public void Hide()
    {
        if (_hidden) return;
        _hidden = true;
        PInvoke.ShowCursor(false);
    }

    public void Show()
    {
        if (!_hidden) return;
        _hidden = false;
        PInvoke.ShowCursor(true);
    }
}
