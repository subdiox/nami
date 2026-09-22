using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Nami.Input;
using Nami.Player;
using Nami.Services;
using Windows.System;

namespace Nami;

public sealed class KeyBindingRow(string key, string command, bool user)
{
    public string Key { get; set; } = key;
    public string Command { get; set; } = command;
    public bool IsUser { get; } = user;
    public string Source => IsUser ? "input.conf" : L.T("mpv default");
}

/// <summary>
/// IINA's key binding editor, simplified: the user's input.conf entries are editable, mpv's
/// built-in bindings are listed for reference, and saved bindings are applied live with
/// mpv's `keybind` command.
/// </summary>
public sealed partial class KeyBindingsDialog : ContentDialog
{
    private static string InputConfPath => Path.Combine(AppSettings.MpvConfigDirectory, "input.conf");

    private readonly List<KeyBindingRow> _user = [];
    private readonly List<KeyBindingRow> _defaults = [];
    private readonly List<string> _otherLines = [];   // comments etc. preserved on save
    private readonly HashSet<string> _removedKeys = new(StringComparer.Ordinal);

    private readonly PlayerViewModel _vm;

    public KeyBindingsDialog(PlayerViewModel vm)
    {
        _vm = vm;
        InitializeComponent();
        LoadUser();
        LoadDefaults();
        Refresh();
        App.Log($"key bindings dialog: user={_user.Count} defaults={_defaults.Count}");
    }

    private void LoadUser()
    {
        if (!File.Exists(InputConfPath)) return;
        foreach (var raw in File.ReadAllLines(InputConfPath))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) { _otherLines.Add(raw); continue; }
            int sp = line.IndexOfAny([' ', '\t']);
            if (sp < 0) { _otherLines.Add(raw); continue; }
            string key = line[..sp];
            string cmd = line[(sp + 1)..].Trim();
            int hash = cmd.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) cmd = cmd[..hash].Trim();
            _user.Add(new KeyBindingRow(key, cmd, true));
        }
    }

    private void LoadDefaults()
    {
        // Active bindings as mpv sees them; entries not from input.conf are the defaults.
        if (_vm.Player?.GetNode("input-bindings") is not List<object?> list) return;
        foreach (var item in list)
        {
            if (item is not Dictionary<string, object?> d) continue;
            string key = d.TryGetValue("key", out var k) ? k as string ?? "" : "";
            string cmd = d.TryGetValue("cmd", out var c) ? c as string ?? "" : "";
            string section = d.TryGetValue("section", out var sec) ? sec as string ?? "" : "";
            if (key.Length == 0 || cmd.Length == 0 || cmd == "ignore") continue;
            if (section.Length > 0 && section != "default") continue;
            if (_user.Any(u => u.Key == key) || _defaults.Any(u => u.Key == key)) continue;
            _defaults.Add(new KeyBindingRow(key, cmd, false));
        }
        _defaults.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase));
    }

    private void Refresh()
    {
        string f = FilterBox.Text.Trim();
        IEnumerable<KeyBindingRow> rows = _user;
        if (ShowDefaults.IsChecked == true) rows = rows.Concat(_defaults);
        if (f.Length > 0)
            rows = rows.Where(r => r.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Command.Contains(f, StringComparison.OrdinalIgnoreCase));
        List.ItemsSource = rows.ToList();
    }

    private void FilterBox_TextChanged(object sender, object e) => Refresh();

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (List.SelectedItem is KeyBindingRow r)
        {
            KeyBox.Text = r.Key;
            CommandBox.Text = r.Command;
            RemoveButton.IsEnabled = r.IsUser;
        }
        else RemoveButton.IsEnabled = false;
    }

    private void KeyBox_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Tab or VirtualKey.Escape) return;
        bool ctrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        string? name = MpvKeyMapper.Map(e.Key, ctrl, alt, shift);
        if (name is null) return;
        KeyBox.Text = name;
        e.Handled = true;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        string key = KeyBox.Text.Trim();
        string cmd = CommandBox.Text.Trim();
        if (key.Length == 0 || cmd.Length == 0) return;
        var existing = _user.FirstOrDefault(r => r.Key == key);
        if (existing is not null) existing.Command = cmd;
        else _user.Add(new KeyBindingRow(key, cmd, true));
        _removedKeys.Remove(key);
        _defaults.RemoveAll(d => d.Key == key);
        Refresh();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not KeyBindingRow r || !r.IsUser) return;
        _user.Remove(r);
        _removedKeys.Add(r.Key);
        Refresh();
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        Directory.CreateDirectory(AppSettings.MpvConfigDirectory);
        var lines = new List<string>(_otherLines);
        if (lines.Count == 0) lines.Add("# Nami key bindings (mpv input.conf syntax)");
        lines.AddRange(_user.Select(r => $"{r.Key} {r.Command}"));
        File.WriteAllText(InputConfPath, string.Join(Environment.NewLine, lines) + Environment.NewLine);

        // Apply live in every window: user bindings override, removed ones fall back to mpv's default table.
        foreach (var w in _vm.Services.Windows.All)
        {
            if (w.Vm.Player is not { } p) continue;
            foreach (var r in _user) p.TryCommand("keybind", r.Key, r.Command);
            foreach (var k in _removedKeys) p.TryCommand("keybind", k, "");
        }
    }
}
