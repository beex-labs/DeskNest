using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeX.DeskNest;

/// <summary>
/// Unified read/write for user config entries in config.json (DeepL key, translation target language, etc.),
/// eliminating duplicated implementations scattered across SettingsWindow / TranslateResultWindow / TranslationService / ScreenCaptureOverlay / CleanerWindow.
/// </summary>
internal static class UserConfigHelper
{
    // ---- DeepL API Key ----

    /// <summary>Reads the user's DeepL API key; returns an empty string if missing or empty.</summary>
    public static string ReadDeepLApiKey()
    {
        return ReadConfigValue("deepl_api_key") ?? "";
    }

    /// <summary>Writes the user's DeepL API key.</summary>
    public static void WriteDeepLApiKey(string key)
    {
        WriteConfigValue("deepl_api_key", key);
    }

    // ---- Translation target language ----

    /// <summary>Reads the translation target language setting (auto/zh/en/ja/ko); defaults to auto.</summary>
    public static string ReadTranslateTarget()
    {
        return (ReadConfigValue("translate_target") ?? "auto").Trim().ToLowerInvariant();
    }

    /// <summary>Writes the translation target language setting.</summary>
    public static void WriteTranslateTarget(string code)
    {
        WriteConfigValue("translate_target", code);
    }

    // ---- Low-level read/write primitives ----

    /// <summary>Reads the string value for the given key from config.json; returns null if missing.</summary>
    private static string? ReadConfigValue(string key)
    {
        try
        {
            var p = BeeXPaths.ConfigFile;
            if (!File.Exists(p)) return null;
            using var s = File.OpenRead(p);
            using var d = JsonDocument.Parse(s);
            return d.RootElement.TryGetProperty(key, out var el) ? el.GetString() : null;
        }
        catch { return null; }
    }

    /// <summary>Writes the given key-value to config.json and mirrors it to the legacy path.</summary>
    private static void WriteConfigValue(string key, string value)
    {
        try
        {
            var p = BeeXPaths.ConfigFile;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            var dict = File.Exists(p)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(File.ReadAllText(p)) ?? new()
                : new Dictionary<string, object>();
            dict[key] = value;
            File.WriteAllText(p, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
            BeeXPaths.MirrorConfigToLegacy();
        }
        catch { }
    }
}
