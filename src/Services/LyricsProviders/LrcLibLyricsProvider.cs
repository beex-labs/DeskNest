using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>LRCLIB lyrics search (international lyrics source).</summary>
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
            // Title-only search (without artist) as the last fallback: enabled only when a duration is available,
            // and requiring the duration to match (diff < 10s) to avoid matching a same-titled song by a different artist
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
                    // Threshold 6 for searches with artist (artist names may differ); raise it to 10 for title-only searches (requires a strong title match)
                    var threshold = urlIndex <= 2 ? 6 : 10;
                    if (bestNode.HasValue && best >= threshold)
                    {
                        var lrc = ReadLrcLibLyrics(bestNode.Value);
                        if (!string.IsNullOrWhiteSpace(lrc)) return ("LRCLIB", lrc);
                    }
                }
            }
            // Last fallback: title-only search + mandatory duration check
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
                            if (LyricsMatching.Normalize(name) != LyricsMatching.Normalize(title)) continue; // title must match exactly
                            if (!item.TryGetProperty("duration", out var d) || !d.TryGetDouble(out var itemSeconds)) continue;
                            var diff = Math.Abs(duration.Value.TotalSeconds - itemSeconds);
                            if (diff > 10) continue; // duration must match (diff < 10s)
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
        // Prefer synced lyrics (with a timeline); if none, fall back to plain-text lyrics (no timeline, but better than "not found")
        if (node.TryGetProperty("syncedLyrics", out var synced) && !string.IsNullOrWhiteSpace(synced.GetString())) return synced.GetString();
        if (node.TryGetProperty("plainLyrics", out var plain) && !string.IsNullOrWhiteSpace(plain.GetString()))
        {
            // Convert each plain-text line to [00:00.00] format so it can be parsed (all timestamps are 0, shown as one block)
            var lines = plain.GetString()!.Replace("\r", "").Split('\n').Where(x => !string.IsNullOrWhiteSpace(x));
            return string.Join("\n", lines.Select(x => "[00:00.00]" + x));
        }
        return null;
    }
}
