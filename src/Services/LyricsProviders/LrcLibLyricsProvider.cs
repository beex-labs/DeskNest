using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>LRCLIB 歌词搜索（国际化歌词源）</summary>
internal static class LrcLibLyricsProvider
{
    public static async Task<(string Provider, string Lrc)?> TryAsync(string title, string artist, TimeSpan? duration, CancellationToken token)
    {
        try
        {
            var seconds = duration.HasValue && duration.Value > TimeSpan.FromSeconds(10) ? ((int)Math.Round(duration.Value.TotalSeconds)).ToString(CultureInfo.InvariantCulture) : "";
            var urls = new List<string>
            {
                "https://lrclib.net/api/get?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist) + (string.IsNullOrWhiteSpace(seconds) ? "" : "&duration=" + seconds),
                "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title) + "&artist_name=" + Uri.EscapeDataString(artist)
            };
            // 纯标题搜索（不带 artist）作为最后兜底：仅当有 duration 时才启用，
            // 且要求 duration 匹配（差<10s），避免匹配到同名不同歌手的歌
            var urlIndex = 0;
            foreach (var url in urls)
            {
                urlIndex++;
                using var response = await MusicLyricsService.Http.GetAsync(url, token);
                if (!response.IsSuccessStatusCode) continue;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (json.RootElement.ValueKind == JsonValueKind.Object)
                {
                    var lrc = ReadLrcLibLyrics(json.RootElement);
                    if (!string.IsNullOrWhiteSpace(lrc)) return ("LRCLIB", lrc);
                }
                else if (json.RootElement.ValueKind == JsonValueKind.Array)
                {
                    JsonElement? bestNode = null; var best = int.MinValue;
                    foreach (var item in json.RootElement.EnumerateArray())
                    {
                        var name = item.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                        var singer = item.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                        var score = LyricsMatching.MatchScore(title, artist, name, singer);
                        if (duration.HasValue && item.TryGetProperty("duration", out var d) && d.TryGetDouble(out var itemSeconds))
                        {
                            var diff = Math.Abs(duration.Value.TotalSeconds - itemSeconds);
                            score += diff < 3 ? 5 : diff < 10 ? 2 : diff < 30 ? 0 : -4;
                        }
                        if (score > best) { best = score; bestNode = item; }
                    }
                    // 带 artist 的搜索阈值 6（歌手名可能有差异）；纯标题搜索阈值提高到 10（要求标题高度匹配）
                    var threshold = urlIndex <= 2 ? 6 : 10;
                    if (bestNode.HasValue && best >= threshold)
                    {
                        var lrc = ReadLrcLibLyrics(bestNode.Value);
                        if (!string.IsNullOrWhiteSpace(lrc)) return ("LRCLIB", lrc);
                    }
                }
            }
            // 最后兜底：纯标题搜索 + duration 强制校验
            if (!string.IsNullOrWhiteSpace(artist) && duration.HasValue && duration.Value > TimeSpan.FromSeconds(20))
            {
                var searchUrl = "https://lrclib.net/api/search?track_name=" + Uri.EscapeDataString(title);
                using var response = await MusicLyricsService.Http.GetAsync(searchUrl, token);
                if (response.IsSuccessStatusCode)
                {
                    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                    if (json.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        JsonElement? bestNode = null; var best = int.MinValue;
                        foreach (var item in json.RootElement.EnumerateArray())
                        {
                            var name = item.TryGetProperty("trackName", out var tn) ? tn.GetString() ?? "" : "";
                            if (LyricsMatching.Normalize(name) != LyricsMatching.Normalize(title)) continue; // 标题必须精确匹配
                            if (!item.TryGetProperty("duration", out var d) || !d.TryGetDouble(out var itemSeconds)) continue;
                            var diff = Math.Abs(duration.Value.TotalSeconds - itemSeconds);
                            if (diff > 10) continue; // duration 必须匹配（差<10s）
                            var singer = item.TryGetProperty("artistName", out var an) ? an.GetString() ?? "" : "";
                            var score = LyricsMatching.MatchScore(title, artist, name, singer) + (diff < 3 ? 5 : 2);
                            if (score > best) { best = score; bestNode = item; }
                        }
                        if (bestNode.HasValue && best >= 10)
                        {
                            var lrc = ReadLrcLibLyrics(bestNode.Value);
                            if (!string.IsNullOrWhiteSpace(lrc)) return ("LRCLIB", lrc);
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public static string? ReadLrcLibLyrics(JsonElement node)
    {
        // 优先返回同步歌词（带时间轴）；若无则返回纯文本歌词（无时间轴，但总比"找不到"好）
        if (node.TryGetProperty("syncedLyrics", out var synced) && !string.IsNullOrWhiteSpace(synced.GetString())) return synced.GetString();
        if (node.TryGetProperty("plainLyrics", out var plain) && !string.IsNullOrWhiteSpace(plain.GetString()))
        {
            // 将纯文本每行转为 [00:00.00] 格式，使其能被 Parse 解析（所有行时间戳为 0，整段显示）
            var lines = plain.GetString()!.Replace("\r", "").Split('\n').Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join("\n", lines.Select(x => "[00:00.00]" + x));
        }
        return null;
    }
}
