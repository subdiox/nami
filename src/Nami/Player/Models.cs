using Nami.Services;
namespace Nami.Player;

public sealed record TrackInfo(long Id, string Type, string? Title, string? Lang, bool Selected, bool External, string? Codec, bool Default, bool AlbumArt = false, bool None = false)
{
    /// <summary>The "&lt;None&gt;" row of a track list (IINA), i.e. the track selector set to "no".</summary>
    public static TrackInfo NoneOf(string type, bool selected) => new(0, type, null, null, selected, false, null, false, None: true);

    public string Display
    {
        get
        {
            if (None) return L.T("<None>");
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Title)) parts.Add(Title);
            if (!string.IsNullOrEmpty(Lang)) parts.Add(Lang);
            if (parts.Count == 0 && !string.IsNullOrEmpty(Codec)) parts.Add(Codec);
            string text = parts.Count == 0 ? L.F("Track {0}", Id) : string.Join(" · ", parts);
            return External ? L.F("{0} (external)", text) : text;
        }
    }

    public string Subtitle => None ? "" : $"#{Id}" + (string.IsNullOrEmpty(Codec) ? "" : $" · {Codec}");
}

public sealed record PlaylistItem(int Index, long Id, string Filename, string? Title, bool Current, bool Playing)
{
    public string Display => string.IsNullOrEmpty(Title) ? Path.GetFileName(Filename) : Title;
    public string Number => (Index + 1).ToString();
}

public sealed record ChapterInfo(int Index, string Title, double Time)
{
    public string Display => string.IsNullOrEmpty(Title) ? L.F("Chapter {0}", Index + 1) : Title;
    public string TimeText => Fmt.Time(Time);
}

public sealed record AudioDeviceInfo(string Name, string Description)
{
    public string Display => Name == "auto" ? L.T("Auto (default device)") : (string.IsNullOrEmpty(Description) ? Name : Description);
}

/// <summary>Display size of the current video (aspect-corrected, rotation applied). 0x0 when unknown.</summary>
public readonly record struct VideoSize(long Width, long Height)
{
    public bool IsValid => Width > 0 && Height > 0;
    public double Aspect => IsValid ? (double)Width / Height : 0;
}

/// <summary>Formatting helpers usable from x:Bind.</summary>
public static class Fmt
{
    public static string Time(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var ts = TimeSpan.FromSeconds(Math.Floor(seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}"
            : $"{ts.Minutes:00}:{ts.Seconds:00}";
    }

    public static string Speed(double speed) => speed.ToString("0.##") + "x";
}
