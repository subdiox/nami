using System.Globalization;
using Microsoft.UI.Xaml.Markup;

namespace Nami.Services;

/// <summary>
/// Localization. Japanese source strings are the keys; other languages map them in
/// <see cref="Translations"/>. Missing entries fall back to the Japanese text, so the
/// app never shows an empty label.
/// </summary>
public static class L
{
    private static string? _lang;
    private static IReadOnlyDictionary<string, string>? _table;

    /// <summary>"ja", "en", … Resolved from settings ("auto" = OS UI language) on first use.</summary>
    public static string Language
    {
        get
        {
            if (_lang is null) Reload();
            return _lang!;
        }
    }

    public static readonly (string code, string name)[] Available =
    [
        ("auto", "自動 / Auto"),
        ("ja", "日本語"),
        ("en", "English"),
    ];

    public static void Reload()
    {
        string setting = App.Settings.Language;
        string lang = setting is "auto" or ""
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ja" ? "ja" : "en")
            : setting;
        _lang = lang;
        _table = lang == "ja" ? null : Translations.For(lang);
    }

    /// <summary>Translate a Japanese source string.</summary>
    public static string T(string ja)
    {
        if (_lang is null) Reload();
        return _table is not null && _table.TryGetValue(ja, out var t) ? t : ja;
    }

    /// <summary>
    /// Translate tooltips and automation names in a live visual tree (attached properties
    /// cannot use the {l:Tr} markup extension).
    /// </summary>
    public static void Localize(Microsoft.UI.Xaml.DependencyObject root)
    {
        if (Language == "ja") return;
        int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
            if (Microsoft.UI.Xaml.Controls.ToolTipService.GetToolTip(child) is string tip)
                Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(child, T(tip));
            string name = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(child);
            if (!string.IsNullOrEmpty(name))
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(child, T(name));
            Localize(child);
        }
    }

    /// <summary>Translate then format.</summary>
    public static string F(string ja, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(ja), args);
}

/// <summary>XAML: Text="{l:Tr Text='クイック設定'}"</summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Text { get; set; } = "";

    protected override object ProvideValue() => L.T(Text);
}
