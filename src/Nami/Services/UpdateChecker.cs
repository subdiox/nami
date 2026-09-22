using System.Net.Http;
using System.Text.Json;

namespace Nami.Services;

public sealed record UpdateInfo(Version Version, string Tag, string Url);

/// <summary>Looks up the newest GitHub release and compares it with the running build.</summary>
public static class UpdateChecker
{
    private const string LatestUrl = "https://api.github.com/repos/subdiox/nami/releases/latest";
    public const string ReleasesUrl = "https://github.com/subdiox/nami/releases";
    public const string WingetId = "subdiox.Nami";

    /// <summary>Version stamped at publish time (0.0.0 for local dev builds).</summary>
    public static Version Current { get; } = Normalize(typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static bool IsDevBuild => Current.Major == 0 && Current.Minor == 0 && Current.Build == 0;

    /// <summary>NAMI_UPDATE_TEST is set: dev builds check too (against the fake version).</summary>
    public static bool TestMode => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NAMI_UPDATE_TEST"));

    public static string CurrentText => Current.ToString(3);

    /// <summary>The latest published release, or null when there is none / the request fails.</summary>
    public static async Task<UpdateInfo?> FetchLatestAsync(CancellationToken ct)
    {
        // Development hook: NAMI_UPDATE_TEST=9.9.9 pretends that version is out.
        if (Environment.GetEnvironmentVariable("NAMI_UPDATE_TEST") is { Length: > 0 } fake && Version.TryParse(fake, out var fv))
            return new UpdateInfo(Normalize(fv), "v" + fake, ReleasesUrl);

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Nami/" + CurrentText);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestUrl, ct));
        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        string url = root.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var v)) return null;
        return new UpdateInfo(Normalize(v), tag, url);
    }

    public static bool IsNewer(UpdateInfo info) => info.Version > Current;

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
}
