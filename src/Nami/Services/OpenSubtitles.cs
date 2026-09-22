using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Security.Credentials;

namespace Nami.Services;

public sealed record SubtitleSearchResult(string FileId, string Language, string Release, string FileName, int Downloads, bool HearingImpaired, double Score)
{
    public string Display => string.IsNullOrEmpty(Release) ? FileName : Release;
    public string Subtitle => $"{Language.ToUpperInvariant()} · {Downloads:N0} DL" + (HearingImpaired ? " · 聴覚障害者向け" : "");
}

/// <summary>
/// OpenSubtitles.com REST API client (IINA uses the same service). Needs a free API key
/// and account; both are kept in the Windows credential vault, not in settings.json.
/// </summary>
public sealed class OpenSubtitles
{
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1/";
    private const string VaultResource = "Nami.OpenSubtitles";
    private const string UserAgent = "Nami v0.1";

    private readonly HttpClient _http = new();
    private string? _token;

    public OpenSubtitles()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    // ---- credentials ---------------------------------------------------------------

    public static (string apiKey, string user, string password) LoadCredentials()
    {
        try
        {
            var vault = new PasswordVault();
            string key = "", user = "", pw = "";
            foreach (var c in vault.FindAllByResource(VaultResource))
            {
                c.RetrievePassword();
                if (c.UserName == "apikey") key = c.Password;
                else { user = c.UserName; pw = c.Password; }
            }
            return (key, user, pw);
        }
        catch { return ("", "", ""); }
    }

    public static void SaveCredentials(string apiKey, string user, string password)
    {
        var vault = new PasswordVault();
        try { foreach (var c in vault.FindAllByResource(VaultResource)) vault.Remove(c); } catch { }
        if (!string.IsNullOrWhiteSpace(apiKey)) vault.Add(new PasswordCredential(VaultResource, "apikey", apiKey.Trim()));
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrEmpty(password))
            vault.Add(new PasswordCredential(VaultResource, user.Trim(), password));
    }

    // ---- API -----------------------------------------------------------------------

    private void Prepare(string apiKey)
    {
        _http.DefaultRequestHeaders.Remove("Api-Key");
        _http.DefaultRequestHeaders.Add("Api-Key", apiKey);
    }

    private async Task EnsureLoginAsync(string apiKey, string user, string password, CancellationToken ct)
    {
        Prepare(apiKey);
        if (_token is not null || string.IsNullOrEmpty(user)) return;
        using var res = await _http.PostAsJsonAsync(BaseUrl + "login", new LoginRequest(user, password), OsJsonContext.Default.LoginRequest, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenSubtitles ログインに失敗しました ({(int)res.StatusCode})");
        var body = await res.Content.ReadFromJsonAsync(OsJsonContext.Default.LoginResponse, ct);
        _token = body?.Token;
        if (_token is null) throw new InvalidOperationException("OpenSubtitles からトークンを受け取れませんでした");
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<List<SubtitleSearchResult>> SearchAsync(string apiKey, string? filePath, string query, string languages, CancellationToken ct)
    {
        Prepare(apiKey);
        var q = new List<string> { "languages=" + Uri.EscapeDataString(languages), "order_by=download_count", "order_direction=desc" };
        if (!string.IsNullOrWhiteSpace(query)) q.Add("query=" + Uri.EscapeDataString(query));
        if (filePath is not null && File.Exists(filePath))
        {
            string? hash = MovieHash(filePath);
            if (hash is not null) q.Add("moviehash=" + hash);
        }
        using var res = await _http.GetAsync(BaseUrl + "subtitles?" + string.Join("&", q), ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException($"OpenSubtitles 検索に失敗しました ({(int)res.StatusCode})");
        var body = await res.Content.ReadFromJsonAsync(OsJsonContext.Default.SearchResponse, ct);
        var list = new List<SubtitleSearchResult>();
        foreach (var d in body?.Data ?? [])
        {
            var a = d.Attributes;
            if (a is null) continue;
            var f = a.Files?.FirstOrDefault();
            if (f is null) continue;
            list.Add(new SubtitleSearchResult(f.FileId.ToString(), a.Language ?? "", a.Release ?? "", f.FileName ?? "",
                a.DownloadCount, a.HearingImpaired, a.MoviehashMatch ? 1 : 0));
        }
        return list.OrderByDescending(r => r.Score).ThenByDescending(r => r.Downloads).ToList();
    }

    /// <summary>Download a subtitle file; returns the saved path.</summary>
    public async Task<string> DownloadAsync(string apiKey, string user, string password, SubtitleSearchResult r, string targetDir, string baseName, CancellationToken ct)
    {
        await EnsureLoginAsync(apiKey, user, password, ct);
        using var res = await _http.PostAsJsonAsync(BaseUrl + "download", new DownloadRequest(long.Parse(r.FileId)), OsJsonContext.Default.DownloadRequest, ct);
        if (!res.IsSuccessStatusCode)
        {
            string msg = (int)res.StatusCode == 406 ? "ダウンロード上限に達しました" : $"({(int)res.StatusCode})";
            throw new InvalidOperationException("OpenSubtitles ダウンロードに失敗しました " + msg);
        }
        var body = await res.Content.ReadFromJsonAsync(OsJsonContext.Default.DownloadResponse, ct);
        if (body?.Link is null) throw new InvalidOperationException("ダウンロードリンクがありません");

        string ext = Path.GetExtension(r.FileName);
        if (string.IsNullOrEmpty(ext)) ext = ".srt";
        Directory.CreateDirectory(targetDir);
        string path = Path.Combine(targetDir, $"{baseName}.{r.Language}{ext}");
        for (int i = 2; File.Exists(path); i++)
            path = Path.Combine(targetDir, $"{baseName}.{r.Language}.{i}{ext}");
        var bytes = await _http.GetByteArrayAsync(body.Link, ct);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    /// <summary>The classic OpenSubtitles hash: file size + first 64 KiB + last 64 KiB as little-endian uint64 sums.</summary>
    public static string? MovieHash(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            long size = fs.Length;
            if (size < 131072) return null;
            ulong hash = (ulong)size;
            var buf = new byte[65536];
            fs.ReadExactly(buf);
            for (int i = 0; i < buf.Length; i += 8) hash += BitConverter.ToUInt64(buf, i);
            fs.Seek(-65536, SeekOrigin.End);
            fs.ReadExactly(buf);
            for (int i = 0; i < buf.Length; i += 8) hash += BitConverter.ToUInt64(buf, i);
            return hash.ToString("x16");
        }
        catch { return null; }
    }

    // ---- DTOs ----------------------------------------------------------------------

    internal sealed record LoginRequest([property: JsonPropertyName("username")] string Username, [property: JsonPropertyName("password")] string Password);
    internal sealed class LoginResponse { [JsonPropertyName("token")] public string? Token { get; set; } }
    internal sealed record DownloadRequest([property: JsonPropertyName("file_id")] long FileId);
    internal sealed class DownloadResponse { [JsonPropertyName("link")] public string? Link { get; set; } }
    internal sealed class SearchResponse { [JsonPropertyName("data")] public List<SearchItem>? Data { get; set; } }
    internal sealed class SearchItem { [JsonPropertyName("attributes")] public SearchAttributes? Attributes { get; set; } }
    internal sealed class SearchAttributes
    {
        [JsonPropertyName("language")] public string? Language { get; set; }
        [JsonPropertyName("release")] public string? Release { get; set; }
        [JsonPropertyName("download_count")] public int DownloadCount { get; set; }
        [JsonPropertyName("hearing_impaired")] public bool HearingImpaired { get; set; }
        [JsonPropertyName("moviehash_match")] public bool MoviehashMatch { get; set; }
        [JsonPropertyName("files")] public List<SearchFile>? Files { get; set; }
    }
    internal sealed class SearchFile
    {
        [JsonPropertyName("file_id")] public long FileId { get; set; }
        [JsonPropertyName("file_name")] public string? FileName { get; set; }
    }
}

[JsonSerializable(typeof(OpenSubtitles.LoginRequest))]
[JsonSerializable(typeof(OpenSubtitles.LoginResponse))]
[JsonSerializable(typeof(OpenSubtitles.DownloadRequest))]
[JsonSerializable(typeof(OpenSubtitles.DownloadResponse))]
[JsonSerializable(typeof(OpenSubtitles.SearchResponse))]
internal partial class OsJsonContext : JsonSerializerContext;
