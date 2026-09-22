using System.Diagnostics;

namespace Nami.Services;

/// <summary>
/// yt-dlp management. mpv's ytdl_hook looks for yt-dlp.exe in its config directory,
/// so that's where we install it; no PATH changes needed.
/// </summary>
public static class YtDlp
{
    private const string DownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

    public static string ExePath => Path.Combine(AppSettings.MpvConfigDirectory, "yt-dlp.exe");

    public static bool IsInstalled => File.Exists(ExePath);

    public static async Task<string?> GetVersionAsync()
    {
        if (!IsInstalled) return null;
        try
        {
            var psi = new ProcessStartInfo(ExePath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return output.Trim();
        }
        catch { return null; }
    }

    /// <summary>Download (or replace) yt-dlp.exe. Reports 0..1 progress.</summary>
    public static async Task InstallOrUpdateAsync(IProgress<double>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(AppSettings.MpvConfigDirectory);
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Nami");
        using var res = await http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        long total = res.Content.Headers.ContentLength ?? -1;
        string tmp = ExePath + ".download";
        await using (var src = await res.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long done = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                done += n;
                if (total > 0) progress?.Report((double)done / total);
            }
        }
        File.Move(tmp, ExePath, overwrite: true);
        progress?.Report(1);
    }

    /// <summary>Is this something mpv would hand to yt-dlp (i.e. not a plain media file URL)?</summary>
    public static bool LooksLikeStreamingSite(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (u.Scheme is not ("http" or "https")) return false;
        string ext = Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
        return ext is "" or ".html" or ".htm" or ".php" or ".aspx";
    }

    public static readonly (string label, string value)[] Formats =
    [
        ("最高画質", "bestvideo+bestaudio/best"),
        ("最大 2160p (4K)", "bestvideo[height<=?2160]+bestaudio/best"),
        ("最大 1440p", "bestvideo[height<=?1440]+bestaudio/best"),
        ("最大 1080p", "bestvideo[height<=?1080]+bestaudio/best"),
        ("最大 720p", "bestvideo[height<=?720]+bestaudio/best"),
        ("最大 480p", "bestvideo[height<=?480]+bestaudio/best"),
        ("音声のみ", "bestaudio/best"),
    ];
}
