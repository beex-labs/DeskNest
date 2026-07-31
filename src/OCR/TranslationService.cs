using System.Net;
using System.Net.Http;
using System.IO;
using System.Text;
using System.Text.Json;

namespace BeeX.OCR;

internal sealed class TranslationService
{
    private const int MaxQueryChars = 450;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    private static string? _cachedDeepLKey;
    private static bool _deepLKeyLoaded;
    private static readonly object DeepLLock = new();

    /// <summary>
    /// Gets the DeepL API key, priority: user key > publisher key > null (falls back to MyMemory).
    /// </summary>
    private static string? GetDeepLApiKey()
    {
        if (_deepLKeyLoaded) return _cachedDeepLKey;
        lock (DeepLLock)
        {
            if (_deepLKeyLoaded) return _cachedDeepLKey;

            // 1. Priority: user-set key (read from config.json)
            string? userKey = UserConfigHelper.ReadDeepLApiKey();
            if (!string.IsNullOrWhiteSpace(userKey))
            {
                _cachedDeepLKey = userKey;
                _deepLKeyLoaded = true;
                return _cachedDeepLKey;
            }

            // 2. Publisher built-in key (hard-coded)
            const string PublisherKey = "448cb35d-6320-4ec4-9451-979a7c560b51:fx";
            if (!string.IsNullOrWhiteSpace(PublisherKey))
            {
                _cachedDeepLKey = PublisherKey;
                _deepLKeyLoaded = true;
                return _cachedDeepLKey;
            }

            // 3. No key, fall back to MyMemory
            _cachedDeepLKey = null;
            _deepLKeyLoaded = true;
            return _cachedDeepLKey;
        }
    }

    /// <summary>
    /// Clears the DeepL key cache so the next GetDeepLApiKey call re-reads it.
    /// </summary>
    public static void ClearDeepLKeyCache()
    {
        lock (DeepLLock)
        {
            _cachedDeepLKey = null;
            _deepLKeyLoaded = false;
        }
    }

    public IReadOnlyList<TranslationLanguageOption> GetTargetLanguages()
    {
        return
        [
            new("中文", "zh-CN"),
            new("英文", "en"),
            new("日文", "ja"),
            new("韩文", "ko"),
            new("繁体中文", "zh-TW")
        ];
    }

    public async Task<string> TranslateAsync(string text, string targetLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        string sourceLanguageCode = DetectSourceLanguage(text, targetLanguageCode);
        if (string.Equals(sourceLanguageCode, targetLanguageCode, StringComparison.OrdinalIgnoreCase))
        {
            return text;
        }

        List<string> chunks = SplitForTranslation(text, MaxQueryChars);
        var translated = new List<string>();
        foreach (string chunk in chunks)
        {
            translated.Add(await TranslateChunkAsync(chunk, sourceLanguageCode, targetLanguageCode));
        }

        return string.Join(Environment.NewLine, translated.Where(part => part.Length > 0)).Trim();
    }

    private static async Task<string> TranslateChunkAsync(string text, string sourceLanguageCode, string targetLanguageCode)
    {
        // 1. Try DeepL
        string? deepLKey = GetDeepLApiKey();
        if (!string.IsNullOrEmpty(deepLKey))
        {
            try
            {
                return await TranslateViaDeepL(text, sourceLanguageCode, targetLanguageCode, deepLKey);
            }
            catch
            {
                // DeepL failed (quota exhausted, network timeout, server error, etc.), fall back to MyMemory
            }
        }

        // 2. Fallback MyMemory
        return await TranslateViaMyMemory(text, sourceLanguageCode, targetLanguageCode);
    }

    private static async Task<string> TranslateViaDeepL(string text, string sourceLanguageCode, string targetLanguageCode, string apiKey)
    {
        string sourceLang = MapToDeepLLangCode(sourceLanguageCode);
        string targetLang = MapToDeepLLangCode(targetLanguageCode);

        var parameters = new Dictionary<string, string>
        {
            ["auth_key"] = apiKey,
            ["text"] = text,
            ["target_lang"] = targetLang
        };

        // source_lang is optional; if omitted, DeepL auto-detects
        if (!string.IsNullOrEmpty(sourceLang))
        {
            parameters["source_lang"] = sourceLang;
        }

        using var content = new FormUrlEncodedContent(parameters);
        using HttpResponseMessage response = await Http.PostAsync("https://api-free.deepl.com/v2/translate", content);

        // HTTP 456 means the DeepL quota is exhausted
        if ((int)response.StatusCode == 456)
        {
            throw new InvalidOperationException("DeepL 翻译额度已耗尽。");
        }

        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);

        JsonElement translations = document.RootElement.GetProperty("translations");
        if (translations.GetArrayLength() > 0)
        {
            string value = translations[0].GetProperty("text").GetString()?.Trim() ?? string.Empty;
            if (value.Length > 0)
            {
                return value;
            }
        }

        throw new InvalidOperationException("DeepL 没有返回有效译文。");
    }

    private static async Task<string> TranslateViaMyMemory(string text, string sourceLanguageCode, string targetLanguageCode)
    {
        string query = Uri.EscapeDataString(text);
        string langPair = Uri.EscapeDataString(sourceLanguageCode + "|" + targetLanguageCode);
        string url = "https://api.mymemory.translated.net/get?q=" + query + "&langpair=" + langPair;

        using HttpResponseMessage response = await Http.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);

        if (document.RootElement.TryGetProperty("responseData", out JsonElement responseData) &&
            responseData.TryGetProperty("translatedText", out JsonElement translatedText))
        {
            string value = WebUtility.HtmlDecode(translatedText.GetString() ?? string.Empty).Trim();
            if (value.Contains("QUERY LENGTH LIMIT EXCEEDED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("翻译内容过长，分段后仍超过在线接口限制。");
            }

            return value;
        }

        throw new InvalidOperationException("翻译服务没有返回有效译文。");
    }

    /// <summary>
    /// Maps internal language codes to DeepL API language codes (uppercase).
    /// </summary>
    private static string MapToDeepLLangCode(string langCode)
    {
        return langCode.ToUpperInvariant() switch
        {
            "ZH-CN" => "ZH",
            "ZH-TW" => "ZH-HANT",
            "EN" => "EN",
            "JA" => "JA",
            "KO" => "KO",
            _ => langCode.ToUpperInvariant()
        };
    }

    private static List<string> SplitForTranslation(string text, int maxChars)
    {
        var chunks = new List<string>();
        var current = new List<string>();
        int currentLength = 0;

        foreach (string rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = rawLine.TrimEnd();
            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            foreach (string piece in SplitLongLine(line, maxChars))
            {
                int addedLength = piece.Length + (current.Count > 0 ? Environment.NewLine.Length : 0);
                if (currentLength > 0 && currentLength + addedLength > maxChars)
                {
                    Flush();
                }

                current.Add(piece);
                currentLength += currentLength == 0 ? piece.Length : addedLength;
            }
        }

        Flush();
        return chunks;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            chunks.Add(string.Join(Environment.NewLine, current));
            current.Clear();
            currentLength = 0;
        }
    }

    private static IEnumerable<string> SplitLongLine(string line, int maxChars)
    {
        string remaining = line.Trim();
        while (remaining.Length > maxChars)
        {
            int cut = FindSplitPoint(remaining, maxChars);
            yield return remaining[..cut].Trim();
            remaining = remaining[cut..].Trim();
        }

        if (remaining.Length > 0)
        {
            yield return remaining;
        }
    }

    private static int FindSplitPoint(string text, int maxChars)
    {
        int limit = Math.Min(maxChars, text.Length - 1);
        for (int i = limit; i >= Math.Max(1, limit - 120); i--)
        {
            if (text[i] is '。' or '，' or ',' or ';' or '；' or '.' or ' ' or '\t')
            {
                return i + 1;
            }
        }

        return limit;
    }

    private static string DetectSourceLanguage(string text, string targetLanguageCode)
    {
        if (ContainsHangul(text))
        {
            return "ko";
        }

        if (ContainsJapaneseKana(text))
        {
            return "ja";
        }

        if (ContainsCjk(text))
        {
            return targetLanguageCode.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "zh-CN";
        }

        return targetLanguageCode.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "zh-CN" : "en";
    }

    private static bool ContainsCjk(string text)
    {
        return text.Any(c => c is >= '\u3400' and <= '\u9fff');
    }

    private static bool ContainsJapaneseKana(string text)
    {
        return text.Any(c => c is >= '\u3040' and <= '\u30ff');
    }

    private static bool ContainsHangul(string text)
    {
        return text.Any(c => c is >= '\uac00' and <= '\ud7af');
    }
}
