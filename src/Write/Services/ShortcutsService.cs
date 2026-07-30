using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BeexWrite.Services;

/// <summary>
/// Manages custom keyboard shortcuts stored in shortcuts.json. Users can edit
/// the file to remap commands; the app reads it on startup and applies the
/// bindings to both the WPF menus (InputGestureText) and the web editor
/// (sent via bridge).
/// </summary>
public sealed class ShortcutsService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private readonly string _path;

    public Dictionary<string, string> Shortcuts { get; private set; } = DefaultShortcuts();

    public ShortcutsService(string settingsDirectory)
    {
        _path = Path.Combine(settingsDirectory, "shortcuts.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts);
                if (loaded is not null && loaded.Count > 0) Shortcuts = loaded;
            }
            else
            {
                Save(); // generate default file for user to edit
            }
        }
        catch
        {
            Shortcuts = DefaultShortcuts();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(Shortcuts, JsonOpts));
        }
        catch { }
    }

    public string? Get(string command) => Shortcuts.GetValueOrDefault(command);

    private static Dictionary<string, string> DefaultShortcuts() => new()
    {
        ["save"] = "Ctrl+S",
        ["saveAs"] = "Ctrl+Shift+S",
        ["open"] = "Ctrl+O",
        ["new"] = "Ctrl+N",
        ["quickOpen"] = "Ctrl+P",
        ["find"] = "Ctrl+F",
        ["toggleSource"] = "Ctrl+/",
        ["toggleSidebar"] = "Ctrl+\\",
        ["bold"] = "Ctrl+B",
        ["italic"] = "Ctrl+I",
        ["underline"] = "Ctrl+U",
        ["strikethrough"] = "Ctrl+Shift+X",
        ["inlineCode"] = "Ctrl+Shift+`",
        ["highlight"] = "Ctrl+Shift+H",
        ["heading1"] = "Ctrl+1",
        ["heading2"] = "Ctrl+2",
        ["heading3"] = "Ctrl+3",
        ["heading4"] = "Ctrl+4",
        ["heading5"] = "Ctrl+5",
        ["heading6"] = "Ctrl+6",
        ["paragraph"] = "Ctrl+0",
        ["quote"] = "Ctrl+Shift+Q",
        ["codeFence"] = "Ctrl+Shift+K",
        ["bulletList"] = "Ctrl+Shift+8",
        ["orderedList"] = "Ctrl+Shift+7",
        ["hardBreak"] = "Shift+Enter",
        ["zoomIn"] = "Ctrl+=",
        ["zoomOut"] = "Ctrl+-",
        ["fullScreen"] = "F11",
        ["print"] = "Ctrl+Shift+P"
    };
}
