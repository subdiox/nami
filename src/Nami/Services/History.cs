using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nami.Services;

public sealed class HistoryEntry
{
    public string Path { get; set; } = "";
    public string Title { get; set; } = "";
    public double Duration { get; set; }
    public double Position { get; set; }
    public DateTime LastPlayed { get; set; }

    [JsonIgnore] public string Display => string.IsNullOrEmpty(Title) ? System.IO.Path.GetFileName(Path) : Title;
    [JsonIgnore] public string Subtitle
    {
        get
        {
            string when = LastPlayed.Date == DateTime.Today ? LastPlayed.ToString("HH:mm") : LastPlayed.ToString("yyyy/MM/dd");
            string pos = Duration > 0 ? $"{Player.Fmt.Time(Position)} / {Player.Fmt.Time(Duration)}" : "";
            return string.IsNullOrEmpty(pos) ? when : $"{when} · {pos}";
        }
    }
    [JsonIgnore] public double Progress => Duration > 0 ? Math.Clamp(Position / Duration, 0, 1) : 0;
    [JsonIgnore] public bool IsUrl => Path.Contains("://");
}

/// <summary>Unlimited playback history (IINA keeps one too), newest first, persisted as JSON.</summary>
public sealed class History
{
    private static string FilePath => Path.Combine(AppSettings.Directory, "history.json");
    private readonly List<HistoryEntry> _entries = [];
    private bool _dirty;

    public ObservableCollection<HistoryEntry> Entries { get; } = [];

    public static History Load()
    {
        var h = new History();
        try
        {
            if (File.Exists(FilePath))
            {
                var list = JsonSerializer.Deserialize(File.ReadAllText(FilePath), HistoryJsonContext.Default.ListHistoryEntry);
                if (list is not null) h._entries.AddRange(list);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"history load failed: {ex.Message}"); }
        h.Refresh();
        return h;
    }

    public HistoryEntry? Find(string path)
        => _entries.FirstOrDefault(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Record that a file started playing (moves it to the top).</summary>
    public HistoryEntry Touch(string path, string title, double duration)
    {
        var e = Find(path);
        if (e is null)
        {
            e = new HistoryEntry { Path = path };
            _entries.Insert(0, e);
        }
        else
        {
            _entries.Remove(e);
            _entries.Insert(0, e);
        }
        if (!string.IsNullOrEmpty(title)) e.Title = title;
        if (duration > 0) e.Duration = duration;
        e.LastPlayed = DateTime.Now;
        _dirty = true;
        Refresh();
        Save();
        return e;
    }

    public void UpdatePosition(string path, double position, double duration)
    {
        var e = Find(path);
        if (e is null) return;
        e.Position = position;
        if (duration > 0) e.Duration = duration;
        _dirty = true;
    }

    public void Remove(HistoryEntry e)
    {
        _entries.Remove(e);
        _dirty = true;
        Refresh();
        Save();
    }

    public void Clear()
    {
        _entries.Clear();
        _dirty = true;
        Refresh();
        Save();
    }

    private void Refresh()
    {
        Entries.Clear();
        foreach (var e in _entries) Entries.Add(e);
    }

    public void Save()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(AppSettings.Directory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, HistoryJsonContext.Default.ListHistoryEntry));
            _dirty = false;
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"history save failed: {ex.Message}"); }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<HistoryEntry>))]
internal partial class HistoryJsonContext : JsonSerializerContext;
