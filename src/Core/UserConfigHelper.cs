using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BeeX.DeskNest;

/// <summary>
/// 统一读写 config.json 中的用户配置项（DeepL Key、翻译目标语言等），
/// 消除散落在 SettingsWindow / TranslateResultWindow / TranslationService / ScreenCaptureOverlay / CleanerWindow 中的重复实现。
/// </summary>
internal static class UserConfigHelper
{
    // ---- DeepL API Key ----

    /// <summary>读取用户设置的 DeepL API Key，不存在或为空返回空字符串。</summary>
    public static string ReadDeepLApiKey()
    {
        return ReadConfigValue("deepl_api_key") ?? "";
    }

    /// <summary>写入用户设置的 DeepL API Key。</summary>
    public static void WriteDeepLApiKey(string key)
    {
        WriteConfigValue("deepl_api_key", key);
    }

    // ---- 翻译目标语言 ----

    /// <summary>读取翻译目标语言设置（auto/zh/en/ja/ko），默认 auto。</summary>
    public static string ReadTranslateTarget()
    {
        return (ReadConfigValue("translate_target") ?? "auto").Trim().ToLowerInvariant();
    }

    /// <summary>写入翻译目标语言设置。</summary>
    public static void WriteTranslateTarget(string code)
    {
        WriteConfigValue("translate_target", code);
    }

    // ---- 底层读写原语 ----

    /// <summary>从 config.json 读取指定 key 的字符串值，不存在返回 null。</summary>
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

    /// <summary>向 config.json 写入指定 key-value 并镜像到旧路径。</summary>
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
