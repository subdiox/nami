using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Nami.Mpv;

namespace Nami;

public sealed class InspectorRow(string name, string value)
{
    public string Name { get; } = name;
    public string Value { get; set; } = value;
}

/// <summary>IINA's Inspector: what mpv knows about the current file, refreshed once a second.</summary>
public sealed partial class InspectorDialog : ContentDialog
{
    private readonly ObservableCollection<InspectorRow> _rows = [];
    private readonly DispatcherQueueTimer _timer;

    private static readonly (string label, string prop, bool osd)[] Props =
    [
        ("ファイル", "path", false),
        ("タイトル", "media-title", false),
        ("コンテナ", "file-format", false),
        ("サイズ", "file-size", true),
        ("長さ", "duration", true),
        ("チャプター数", "chapter-list/count", false),
        ("", "", false),
        ("映像コーデック", "video-codec", false),
        ("解像度", "video-params/w", false),
        ("表示解像度", "video-params/dw", false),
        ("ピクセル形式", "video-params/pixelformat", false),
        ("色域 / 伝達関数", "video-params/primaries", false),
        ("色域 (matrix)", "video-params/colormatrix", false),
        ("フレームレート", "container-fps", false),
        ("実フレームレート", "estimated-vf-fps", false),
        ("映像ビットレート", "video-bitrate", true),
        ("ハードウェアデコード", "hwdec-current", false),
        ("", "", false),
        ("音声コーデック", "audio-codec", false),
        ("サンプルレート", "audio-params/samplerate", false),
        ("チャンネル", "audio-params/channels", false),
        ("音声フォーマット", "audio-params/format", false),
        ("音声ビットレート", "audio-bitrate", true),
        ("出力デバイス", "audio-device", false),
        ("", "", false),
        ("キャッシュ", "demuxer-cache-duration", true),
        ("ドロップしたフレーム", "frame-drop-count", false),
        ("A/V 同期ずれ", "avsync", false),
    ];

    public InspectorDialog()
    {
        InitializeComponent();
        Rows.ItemsSource = _rows;
        Refresh();
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    private void Refresh()
    {
        var p = App.Vm.Player;
        if (p is null) return;
        var values = new List<InspectorRow>();
        foreach (var (label, prop, osd) in Props)
        {
            if (prop.Length == 0) { values.Add(new InspectorRow("", "")); continue; }
            string? v = osd ? OsdString(p, prop) : p.GetString(prop);
            if (prop == "video-params/w")
            {
                string? h = p.GetString("video-params/h");
                v = v is null ? null : $"{v} × {h}";
            }
            else if (prop == "video-params/dw")
            {
                string? h = p.GetString("video-params/dh");
                string? a = p.GetString("video-params/aspect");
                v = v is null ? null : $"{v} × {h}" + (a is null ? "" : $"  (aspect {double.Parse(a, System.Globalization.CultureInfo.InvariantCulture):0.###})");
            }
            else if (prop == "video-params/primaries")
            {
                string? g = p.GetString("video-params/gamma");
                v = v is null ? null : $"{v} / {g}";
            }
            if (string.IsNullOrEmpty(v)) continue;
            values.Add(new InspectorRow(label, v));
        }

        // metadata tags
        var meta = MetadataPairs(p);
        if (meta.Count > 0)
        {
            values.Add(new InspectorRow("", ""));
            values.AddRange(meta.Select(kv => new InspectorRow(kv.Key, kv.Value)));
        }

        // In-place update keeps the scroll position.
        for (int i = 0; i < values.Count; i++)
        {
            if (i < _rows.Count)
            {
                if (_rows[i].Name != values[i].Name) _rows[i] = values[i];
                else if (_rows[i].Value != values[i].Value) _rows[i] = values[i];
            }
            else _rows.Add(values[i]);
        }
        while (_rows.Count > values.Count) _rows.RemoveAt(_rows.Count - 1);
    }

    private static unsafe string? OsdString(MpvPlayer p, string prop)
    {
        byte* s = LibMpv.mpv_get_property_osd_string(p.Handle, prop);
        if (s == null) return null;
        try { return LibMpv.Utf8(s); }
        finally { LibMpv.mpv_free(s); }
    }

    private static List<KeyValuePair<string, string>> MetadataPairs(MpvPlayer p)
    {
        var list = new List<KeyValuePair<string, string>>();
        string? count = p.GetString("metadata/list/count");
        if (!int.TryParse(count, out int n)) return list;
        for (int i = 0; i < Math.Min(n, 40); i++)
        {
            string? k = p.GetString($"metadata/list/{i}/key");
            string? v = p.GetString($"metadata/list/{i}/value");
            if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) list.Add(new(k, v.Length > 300 ? v[..300] + "…" : v));
        }
        return list;
    }
}
