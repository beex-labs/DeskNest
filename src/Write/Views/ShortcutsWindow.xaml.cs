using System.Collections.Generic;
using System.Linq;
using System.Windows;
using BeexWrite.Services;

namespace BeexWrite.Views;

public partial class ShortcutsWindow : Window
{
    public ShortcutsWindow(ShortcutsService shortcuts)
    {
        Services.ThemeService.Attach(this);
        InitializeComponent();

        var items = shortcuts.Shortcuts
            .OrderBy(kv => kv.Key)
            .Select(kv => new ShortcutEntry { Command = FormatCommand(kv.Key), Key = kv.Value })
            .ToList();

        ShortcutList.ItemsSource = items;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private static string FormatCommand(string raw)
    {
        // Convert camelCase to Title Case with spaces
        var result = System.Text.RegularExpressions.Regex.Replace(raw, "([a-z])([A-Z])", "$1 $2");
        if (result.Length > 0)
            result = char.ToUpper(result[0]) + result[1..];
        return result;
    }
}

public sealed class ShortcutEntry
{
    public string Command { get; set; } = "";
    public string Key { get; set; } = "";
}
