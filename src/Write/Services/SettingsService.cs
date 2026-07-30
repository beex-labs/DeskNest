using System;
using System.IO;
using System.Text.Json;
using BeexWrite.Models;

namespace BeexWrite.Services;

/// <summary>Loads and persists <see cref="AppSettings"/> as JSON.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string SettingsDirectory { get; }
    public string SettingsPath { get; }

    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        // Embedded mode: settings live under the DeskNest data root.
        SettingsDirectory = WriteHost.WriteDataDirectory;
        SettingsPath = Path.Combine(SettingsDirectory, "settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null) Settings = loaded;
            }
        }
        catch
        {
            // Corrupt settings must never block startup; fall back to defaults.
            Settings = new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDirectory);
            var json = JsonSerializer.Serialize(Settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Best-effort persistence; ignore IO failures.
        }
    }

    public void AddRecentFile(string path)
    {
        AddRecent(Settings.RecentFiles, path);
    }

    public void AddRecentFolder(string path)
    {
        AddRecent(Settings.RecentFolders, path);
    }

    private static void AddRecent(System.Collections.Generic.List<string> list, string path)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        while (list.Count > AppSettings.MaxRecent) list.RemoveAt(list.Count - 1);
    }
}
