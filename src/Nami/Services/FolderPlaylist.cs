using System.Text.RegularExpressions;

namespace Nami.Services;

/// <summary>IINA's "auto load files in the same folder": media files next to the opened one, in natural order.</summary>
public static partial class FolderPlaylist
{
    private static readonly HashSet<string> MediaExtensions =
        new(FileAssociation.VideoExtensions.Concat(FileAssociation.AudioExtensions), StringComparer.OrdinalIgnoreCase);

    public static List<string> Siblings(string filePath)
    {
        var result = new List<string>();
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (dir is null || !Directory.Exists(dir)) return result;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (MediaExtensions.Contains(Path.GetExtension(f))) result.Add(f);
            }
            result.Sort(NaturalCompare);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"folder scan failed: {ex.Message}"); }
        return result;
    }

    [GeneratedRegex(@"\d+|\D+")]
    private static partial Regex Chunks();

    /// <summary>"ep2" &lt; "ep10": compare digit runs numerically, everything else case-insensitively.</summary>
    public static int NaturalCompare(string a, string b)
    {
        var ca = Chunks().Matches(Path.GetFileName(a));
        var cb = Chunks().Matches(Path.GetFileName(b));
        int n = Math.Min(ca.Count, cb.Count);
        for (int i = 0; i < n; i++)
        {
            string x = ca[i].Value, y = cb[i].Value;
            int c;
            if (char.IsDigit(x[0]) && char.IsDigit(y[0]))
            {
                c = x.TrimStart('0').Length.CompareTo(y.TrimStart('0').Length);
                if (c == 0) c = string.CompareOrdinal(x.TrimStart('0'), y.TrimStart('0'));
            }
            else c = string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
            if (c != 0) return c;
        }
        return ca.Count.CompareTo(cb.Count);
    }
}
