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
