using System.Net.Http;
using System.Text.Json;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>网易云音乐歌词搜索</summary>
internal static class NetEaseLyricsProvider
{
    public static async Task<(string Provider, string Lrc)?> TryAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            foreach (var query in LyricsMatching.SearchQueries(title, artist))
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/search/get")
                {
                    Content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["s"] = query, ["type"] = "1", ["limit"] = "12", ["offset"] = "0"
                    })
                };
                request.Headers.Referrer = new Uri("https://music.163.com/");
                using var response = await MusicLyricsService.Http.SendAsync(request, token);
                response.EnsureSuccessStatusCode();
                using var search = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!search.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) continue;
                long id = 0; var best = int.MinValue;
                foreach (var song in songs.EnumerateArray())
                {
                    var name = song.GetProperty("name").GetString() ?? "";
                    var singer = song.TryGetProperty("artists", out var artists) && artists.GetArrayLength() > 0 ? string.Join(" / ", artists.EnumerateArray().Select(x => x.GetProperty("name").GetString() ?? "").Where(x => !string.IsNullOrWhiteSpace(x))) : "";
                    var score = LyricsMatching.MatchScore(title, artist, name, singer);
                    if (score > best) { best = score; id = song.GetProperty("id").GetInt64(); }
                }
                if (id == 0 || best < 8) continue;
                using var lyricRequest = new HttpRequestMessage(HttpMethod.Get, $"https://music.163.com/api/song/lyric?id={id}&lv=1&kv=1&tv=-1");
                lyricRequest.Headers.Referrer = new Uri("https://music.163.com/");
                using var lyricResponse = await MusicLyricsService.Http.SendAsync(lyricRequest, token);
                lyricResponse.EnsureSuccessStatusCode();
                using var lyricJson = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync(token));
                var lrc = lyricJson.RootElement.TryGetProperty("lrc", out var lrcNode) && lrcNode.TryGetProperty("lyric", out var lyric) ? lyric.GetString() : null;
                if (!string.IsNullOrWhiteSpace(lrc)) return ("網易雲音樂", lrc);
            }
            return null;
        }
        catch { return null; }
    }
}
