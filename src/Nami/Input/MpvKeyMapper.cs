using Windows.System;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Nami.Input;

/// <summary>
/// Translates WinUI key events into mpv key names ("Ctrl+LEFT", "a", "SHARP", ...)
/// so mpv's own input.conf bindings drive playback.
/// </summary>
internal static unsafe class MpvKeyMapper
{
    public static string? Map(VirtualKey key, bool ctrl, bool alt, bool shift)
    {
        bool special = true;
        string? name = key switch
        {
            VirtualKey.Space => "SPACE",
            VirtualKey.Enter => "ENTER",
            VirtualKey.Escape => "ESC",
            VirtualKey.Tab => "TAB",
            VirtualKey.Back => "BS",
            VirtualKey.Delete => "DEL",
            VirtualKey.Insert => "INS",
            VirtualKey.Home => "HOME",
            VirtualKey.End => "END",
            VirtualKey.PageUp => "PGUP",
            VirtualKey.PageDown => "PGDWN",
            VirtualKey.Left => "LEFT",
            VirtualKey.Right => "RIGHT",
            VirtualKey.Up => "UP",
            VirtualKey.Down => "DOWN",
            VirtualKey.Print => "PRINT",
            VirtualKey.Pause => "PAUSE",
            VirtualKey.Menu => null,
            VirtualKey.Control => null,
            VirtualKey.Shift => null,
            VirtualKey.LeftWindows or VirtualKey.RightWindows => null,
            VirtualKey.CapitalLock or VirtualKey.NumberKeyLock or VirtualKey.Scroll => null,
            >= VirtualKey.F1 and <= VirtualKey.F24 => "F" + (key - VirtualKey.F1 + 1),
            >= VirtualKey.NumberPad0 and <= VirtualKey.NumberPad9 => "KP" + (key - VirtualKey.NumberPad0),
            VirtualKey.Decimal => "KP_DEC",
            VirtualKey.Add => "KP_ADD",
            VirtualKey.Subtract => "KP_SUBTRACT",
            VirtualKey.Multiply => "KP_MULTIPLY",
            VirtualKey.Divide => "KP_DIVIDE",
            VirtualKey.GoBack => "BACK",
            VirtualKey.GoForward => "FORWARD",
            VirtualKey.Stop => "STOP",
            VirtualKey.Refresh => "REFRESH",
            _ => null,
        };

        if (name is null)
        {
            if (IsModifierOnly(key)) return null;
            char c = ToChar(key);
            if (c == '\0' || char.IsControl(c)) return null;
            special = false;
            name = c switch
            {
                '#' => "SHARP",
                ' ' => "SPACE",
                _ => c.ToString(),
            };
        }

        string prefix = "";
        if (ctrl) prefix += "Ctrl+";
        if (alt) prefix += "Alt+";
        if (shift && special) prefix += "Shift+";   // for characters, shift is already in the char
        return prefix + name;
    }

    private static bool IsModifierOnly(VirtualKey key) => key is VirtualKey.Shift or VirtualKey.Control or VirtualKey.Menu
        or VirtualKey.LeftShift or VirtualKey.RightShift or VirtualKey.LeftControl or VirtualKey.RightControl
        or VirtualKey.LeftMenu or VirtualKey.RightMenu;

    /// <summary>Resolve the character the key would produce with the current layout and Shift/CapsLock state (Ctrl/Alt masked out).</summary>
    private static char ToChar(VirtualKey key)
    {
        var state = stackalloc byte[256];
        if (!PInvoke.GetKeyboardState(new Span<byte>(state, 256))) return '\0';
        state[(int)VirtualKey.Control] = 0;
        state[(int)VirtualKey.LeftControl] = 0;
        state[(int)VirtualKey.RightControl] = 0;
        state[(int)VirtualKey.Menu] = 0;
        state[(int)VirtualKey.LeftMenu] = 0;
        state[(int)VirtualKey.RightMenu] = 0;

        uint scan = PInvoke.MapVirtualKey((uint)key, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        var buf = stackalloc char[8];
        // Bit 2 (0x4): do not change keyboard state (avoids dead-key side effects).
        int n = PInvoke.ToUnicode((uint)key, scan, new ReadOnlySpan<byte>(state, 256), new Span<char>(buf, 8), 0x4);
        return n == 1 ? buf[0] : '\0';
    }
}
