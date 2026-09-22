using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nami.Services;

public enum OscLayout
{
    Floating,
    Bottom,
    Top,
}

public enum HdrMode
{
    /// <summary>Pass HDR through when the display is in HDR mode, otherwise tone-map to SDR.</summary>
    Auto,
    /// <summary>Always tone-map to SDR.</summary>
    Sdr,
    /// <summary>Always output HDR10 (PQ / BT.2020).</summary>
    Passthrough,
}

public sealed class AppSettings
{
    public double Volume { get; set; } = 100;
    public bool Muted { get; set; }
    public HdrMode HdrMode { get; set; } = HdrMode.Auto;
    public bool RememberVolume { get; set; } = true;
    public bool ResizeWindowToVideo { get; set; } = true;
    public bool ResumePlayback { get; set; } = true;
    public bool AutoLoadFolder { get; set; } = true;
    public bool KeepHistory { get; set; } = true;
    public string SubtitleLanguages { get; set; } = "ja,en";
    public bool SeekThumbnails { get; set; } = true;
    public string YtdlFormat { get; set; } = "bestvideo[height<=?1080]+bestaudio/best";
    public OscLayout OscLayout { get; set; } = OscLayout.Floating;
    public bool AutoMusicMode { get; set; } = true;
    public string ScreenshotDirectory { get; set; } = "";
    public string ScreenshotFormat { get; set; } = "png";
    public double[] EqGains { get; set; } = new double[10];
    public bool EqEnabled { get; set; }
    public SubtitleStyle Subtitles { get; set; } = new();

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nami");

    public static string MpvConfigDirectory { get; } = Path.Combine(Directory, "mpv");

    private static string FilePath => Path.Combine(Directory, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SettingsJsonContext.Default.AppSettings) ?? new();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"settings load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, SettingsJsonContext.Default.AppSettings));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"settings save failed: {ex.Message}");
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(SubtitleStyle))]
internal partial class SettingsJsonContext : JsonSerializerContext;
