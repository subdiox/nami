using Microsoft.Win32;
using Nami.Mpv;

namespace Nami.Services;

/// <summary>Global subtitle appearance (IINA: Preferences > Subtitles), mapped onto mpv's sub-* options.</summary>
public sealed class SubtitleStyle
{
    public string Font { get; set; } = "";               // "" = mpv default (sans-serif)
    public double Size { get; set; } = 38;               // sub-font-size
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public string Color { get; set; } = "#FFFFFFFF";      // #AARRGGBB
    public string OutlineColor { get; set; } = "#FF000000";
    public double OutlineSize { get; set; } = 3;
    public double ShadowOffset { get; set; }
    public string BackColor { get; set; } = "#00000000";
    public string BorderStyle { get; set; } = "outline-and-shadow"; // outline-and-shadow | opaque-box | background-box
    public string Codepage { get; set; } = "auto";
    public string AssOverride { get; set; } = "yes";      // no | yes | scale | force | strip
    public long Position { get; set; } = 100;             // sub-pos
    public double Scale { get; set; } = 1;                // sub-scale

    public void Apply(MpvPlayer p)
    {
        Set(p, "sub-font", string.IsNullOrWhiteSpace(Font) ? "sans-serif" : Font);
        Set(p, "sub-font-size", Size.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(p, "sub-bold", Bold ? "yes" : "no");
        Set(p, "sub-italic", Italic ? "yes" : "no");
        Set(p, "sub-color", Color);
        Set(p, "sub-outline-color", OutlineColor);
        Set(p, "sub-outline-size", OutlineSize.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(p, "sub-shadow-offset", ShadowOffset.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Set(p, "sub-back-color", BackColor);
        Set(p, "sub-border-style", BorderStyle);
        Set(p, "sub-codepage", Codepage);
        Set(p, "sub-ass-override", AssOverride);
        Set(p, "sub-pos", Position.ToString());
        Set(p, "sub-scale", Scale.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private static void Set(MpvPlayer p, string name, string value)
    {
        try { p.SetProperty(name, value); }
        catch (MpvException ex) { System.Diagnostics.Debug.WriteLine($"{name}={value}: {ex.Message}"); }
    }

    public static readonly string[] Codepages =
        ["auto", "utf-8", "cp932", "shift_jis", "euc-jp", "iso-2022-jp", "gbk", "big5", "euc-kr", "cp1252", "cp1251", "iso-8859-1"];

    /// <summary>Installed font family names, from the registry (no DirectWrite dependency).</summary>
    public static List<string> SystemFonts()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            try
            {
                using var key = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts");
                if (key is null) continue;
                foreach (var v in key.GetValueNames())
                {
                    string n = v;
                    int paren = n.IndexOf(" (", StringComparison.Ordinal);
                    if (paren > 0) n = n[..paren];
                    // "Segoe UI Bold" etc. are styles of one family; keep the family only.
                    foreach (var suffix in new[] { " Bold Italic", " Bold", " Italic", " Light", " Semibold", " Semilight", " Black", " Regular" })
                        if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { n = n[..^suffix.Length]; break; }
                    if (n.Length > 0) names.Add(n);
                }
            }
            catch { }
        }
        return names.ToList();
    }
}
