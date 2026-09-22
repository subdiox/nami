using System.Globalization;
using Nami.Interop;
using Nami.Mpv;
using Nami.Services;

namespace Nami.Player;

/// <summary>
/// In composition mode mpv has no window and therefore cannot see what the display
/// supports. We query DXGI ourselves and hand mpv the target parameters.
/// </summary>
public static class HdrController
{
    public static DisplayInfo? LastDisplay { get; private set; }
    public static bool IsPassthroughActive { get; private set; }

    public static void Apply(MpvPlayer player, nint hwnd, HdrMode mode)
    {
        DisplayInfo? display = null;
        try { display = DisplayInfo.Query(hwnd); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"DisplayInfo failed: {ex}"); }
        LastDisplay = display;

        bool passthrough = mode switch
        {
            HdrMode.Passthrough => true,
            HdrMode.Sdr => false,
            _ => display?.IsHdr == true,
        };
        IsPassthroughActive = passthrough;

        if (passthrough)
        {
            float peak = display is { IsHdr: true, MaxLuminance: > 0 } ? display.MaxLuminance : 1000f;
            float sdrWhite = display is { SdrWhiteNits: > 0 } ? display.SdrWhiteNits : 203f;
            Set(player, "target-colorspace-hint", "yes");
            Set(player, "target-trc", "pq");
            Set(player, "target-prim", "bt.2020");
            Set(player, "target-peak", peak.ToString("F0", CultureInfo.InvariantCulture));
            Set(player, "target-contrast", "inf");
            Set(player, "hdr-reference-white", sdrWhite.ToString("F0", CultureInfo.InvariantCulture));
            Set(player, "d3d11-output-format", "rgb10_a2");
        }
        else
        {
            Set(player, "target-colorspace-hint", "no");
            Set(player, "target-trc", "auto");
            Set(player, "target-prim", "auto");
            Set(player, "target-peak", "auto");
            Set(player, "target-contrast", "auto");
            Set(player, "hdr-reference-white", "auto");
            Set(player, "d3d11-output-format", "auto");
        }

        System.Diagnostics.Debug.WriteLine($"HDR: mode={mode} passthrough={passthrough} display={display}");
    }

    private static void Set(MpvPlayer player, string name, string value)
    {
        try { player.SetProperty(name, value); }
        catch (MpvException ex) { System.Diagnostics.Debug.WriteLine($"{name}={value}: {ex.Message}"); }
    }
}
