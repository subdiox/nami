using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.Shell;

namespace Nami.Services;

/// <summary>
/// Registers Nami as a media application in HKCU so it shows up in Explorer's
/// "Open with" menu and in Settings > Apps > Default apps. Making it the default
/// is up to the user (Windows does not allow apps to take defaults silently).
/// </summary>
public static class FileAssociation
{
    public const string ProgId = "Nami.Media";
    /// <summary>namiplayer://open?url=&lt;encoded&gt;  or  namiplayer://&lt;url&gt; — for bookmarklets and other apps.</summary>
    public const string UrlScheme = "namiplayer";

    public static string? ParseSchemeUrl(string arg)
    {
        string rest = arg[(UrlScheme.Length + 3)..];
        if (rest.StartsWith("open?", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var kv in rest[5..].Split('&'))
            {
                int eq = kv.IndexOf('=');
                if (eq > 0 && kv[..eq].Equals("url", StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(kv[(eq + 1)..]);
            }
            return null;
        }
        return Uri.UnescapeDataString(rest);
    }
    private const string AppName = "Nami";

    public static readonly string[] VideoExtensions =
    [
        ".mkv", ".mp4", ".m4v", ".mov", ".avi", ".webm", ".ts", ".m2ts", ".mts", ".flv", ".wmv", ".mpg", ".mpeg",
        ".vob", ".3gp", ".ogv", ".rm", ".rmvb", ".divx", ".asf",
    ];

    public static readonly string[] AudioExtensions =
    [
        ".mp3", ".flac", ".m4a", ".aac", ".opus", ".ogg", ".oga", ".wav", ".wma", ".ape", ".tta", ".wv", ".mka", ".dsf",
    ];

    public static readonly string[] PlaylistExtensions = [".m3u", ".m3u8", ".pls"];

    public static IEnumerable<string> AllExtensions => VideoExtensions.Concat(AudioExtensions).Concat(PlaylistExtensions);

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("no process path");

    public static bool IsRegistered
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications");
            return key?.GetValue(AppName) is string;
        }
    }

    public static void Register()
    {
        string exe = ExePath;
        string openCommand = $"\"{exe}\" \"%1\"";

        // ProgID
        using (var prog = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ProgId}"))
        {
            prog.SetValue("", "メディアファイル (Nami)");
            prog.SetValue("FriendlyTypeName", "メディアファイル (Nami)");
            using var icon = prog.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var cmd = prog.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", openCommand);
            using var open = prog.CreateSubKey(@"shell\open");
            open.SetValue("", "Nami で再生");
        }

        // Applications\Nami.exe (drives the "Open with" list)
        using (var app = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Applications\Nami.exe"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using var cmd = app.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", openCommand);
            using var types = app.CreateSubKey("SupportedTypes");
            foreach (var ext in AllExtensions) types.SetValue(ext, "");
        }

        // Capabilities (drives Settings > Default apps)
        using (var caps = Registry.CurrentUser.CreateSubKey(@"Software\Nami\Capabilities"))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "mpv ベースのメディアプレイヤー");
            using var assoc = caps.CreateSubKey("FileAssociations");
            foreach (var ext in AllExtensions) assoc.SetValue(ext, ProgId);
        }
        using (var reg = Registry.CurrentUser.CreateSubKey(@"Software\RegisteredApplications"))
            reg.SetValue(AppName, @"Software\Nami\Capabilities");

        // namiplayer:// URL protocol
        using (var scheme = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{UrlScheme}"))
        {
            scheme.SetValue("", "URL:Nami Player");
            scheme.SetValue("URL Protocol", "");
            using var icon = scheme.CreateSubKey("DefaultIcon");
            icon.SetValue("", $"\"{exe}\",0");
            using var cmd = scheme.CreateSubKey(@"shell\open\command");
            cmd.SetValue("", openCommand);
        }

        // Per-extension "Open with" entries
        foreach (var ext in AllExtensions)
        {
            using var k = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{ext}\OpenWithProgids");
            k.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        }

        NotifyShell();
    }

    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{ProgId}", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\Applications\Nami.exe", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Nami\Capabilities", throwOnMissingSubKey: false);
        Registry.CurrentUser.DeleteSubKeyTree($@"Software\Classes\{UrlScheme}", throwOnMissingSubKey: false);
        using (var reg = Registry.CurrentUser.OpenSubKey(@"Software\RegisteredApplications", writable: true))
            reg?.DeleteValue(AppName, throwOnMissingValue: false);
        foreach (var ext in AllExtensions)
        {
            using var k = Registry.CurrentUser.OpenSubKey($@"Software\Classes\{ext}\OpenWithProgids", writable: true);
            k?.DeleteValue(ProgId, throwOnMissingValue: false);
        }
        NotifyShell();
    }

    private static unsafe void NotifyShell()
        => PInvoke.SHChangeNotify(SHCNE_ID.SHCNE_ASSOCCHANGED, SHCNF_FLAGS.SHCNF_IDLIST, null, null);
}
