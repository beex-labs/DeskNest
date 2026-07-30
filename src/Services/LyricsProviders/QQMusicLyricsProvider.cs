using System.Net.Http;
using System.Text.Json;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>QQ音乐歌词搜索</summary>
internal static class QQMusicLyricsProvider
{
    public static async Task<(string Provider, string Lrc)?> TryAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            foreach (var query in LyricsMatching.SearchQueries(title, artist))
            {
            var searchUrl = "https://c.y.qq.com/splcloud/fcgi-bin/smartbox_new.fcg?format=json&key=" + Uri.EscapeDataString(query);
            using var request = new HttpRequestMessage(HttpMethod.Get, searchUrl);
            request.Headers.Referrer = new Uri("https://y.qq.com/");
            using var response = await MusicLyricsService.Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var search = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!search.RootElement.GetProperty("data").GetProperty("song").TryGetProperty("itemlist", out var songs)) return null;
            string mid = ""; var best = int.MinValue;
            foreach (var song in songs.EnumerateArray())
            {
                var name = song.GetProperty("name").GetString() ?? "";
                var singer = song.GetProperty("singer").GetString() ?? "";
                var score = LyricsMatching.MatchScore(title, artist, name, singer);
                if (score > best) { best = score; mid = song.GetProperty("mid").GetString() ?? ""; }
            }
            if (string.IsNullOrWhiteSpace(mid) || best < 8) continue;
            var lyricUrl = "https://c.y.qq.com/lyric/fcgi-bin/fcg_query_lyric_new.fcg?format=json&nobase64=1&songmid=" + Uri.EscapeDataString(mid);
            using var lyricRequest = new HttpRequestMessage(HttpMethod.Get, lyricUrl);
            lyricRequest.Headers.Referrer = new Uri("https://y.qq.com/");
            using var lyricResponse = await MusicLyricsService.Http.SendAsync(lyricRequest, token);
            lyricResponse.EnsureSuccessStatusCode();
            using var lyricJson = JsonDocument.Parse(await lyricResponse.Content.ReadAsStringAsync(token));
            var lrc = lyricJson.RootElement.TryGetProperty("lyric", out var lyric) ? lyric.GetString() : null;
            if (!string.IsNullOrWhiteSpace(lrc)) return ("QQ 音樂", lrc);
            }
            return null;
        }
        catch { return null; }
    }
}
