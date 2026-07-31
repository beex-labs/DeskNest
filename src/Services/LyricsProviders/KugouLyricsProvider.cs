using System.Net.Http;
using System.Text.Json;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>Kugou lyrics search: covers older Chinese songs, web songs and covers.</summary>
internal static class KugouLyricsProvider
{
    public static async Task<(string Provider, string Lrc)?> TryAsync(string title, string artist, CancellationToken token)
    {
        try
        {
            foreach (var query in LyricsMatching.SearchQueries(title, artist))
            {
                // Step 1: search
                var searchUrl=$"http://mobilecdn.kugou.com/api/v3/search/song?format=json&keyword={Uri.EscapeDataString(query)}&page=1&pagesize=5";
                using var searchResp=await MusicLyricsService.Http.GetAsync(searchUrl,token);
                if(!searchResp.IsSuccessStatusCode)continue;
                using var searchDoc=JsonDocument.Parse(await searchResp.Content.ReadAsStringAsync(token));
                if(!searchDoc.RootElement.TryGetProperty("data",out var data)||!data.TryGetProperty("info",out var info))continue;
                string? bestHash=null;var bestScore=int.MinValue;
                foreach(var song in info.EnumerateArray())
                {
                    var name=song.TryGetProperty("songname",out var sn)?sn.GetString()??"":"";
                    var singer=song.TryGetProperty("singername",out var sa)?sa.GetString()??"":"";
                    var score=LyricsMatching.MatchScore(title,artist,name,singer);
                    if(score>bestScore){bestScore=score;bestHash=song.TryGetProperty("hash",out var h)?h.GetString():null;}
                }
                if(string.IsNullOrWhiteSpace(bestHash)||bestScore<8)continue;
                // Step 2: fetch lyrics by hash
                var lyricUrl=$"https://krcs.kugou.com/search?ver=1&man=yes&client=mobi&hash={bestHash}";
                using var lyricResp=await MusicLyricsService.Http.GetAsync(lyricUrl,token);
                if(!lyricResp.IsSuccessStatusCode)continue;
                using var lyricDoc=JsonDocument.Parse(await lyricResp.Content.ReadAsStringAsync(token));
                if(!lyricDoc.RootElement.TryGetProperty("candidates",out var candidates)||candidates.GetArrayLength()==0)continue;
                var first=candidates[0];
                var lrcId=first.TryGetProperty("id",out var lid)?lid.GetString():"";
                var accessKey=first.TryGetProperty("accesskey",out var ak)?ak.GetString():"";
                if(string.IsNullOrWhiteSpace(lrcId)||string.IsNullOrWhiteSpace(accessKey))continue;
                // Step 3: download lyrics
                var dlUrl=$"https://lyrics.kugou.com/download?ver=1&client=pc&id={lrcId}&accesskey={accessKey}&fmt=lrc&charset=utf8";
                using var dlResp=await MusicLyricsService.Http.GetAsync(dlUrl,token);
                if(!dlResp.IsSuccessStatusCode)continue;
                using var dlDoc=JsonDocument.Parse(await dlResp.Content.ReadAsStringAsync(token));
                var lrc=dlDoc.RootElement.TryGetProperty("content",out var c)?c.GetString():"";
                if(!string.IsNullOrWhiteSpace(lrc))return ("酷狗音樂",lrc);
            }
            return null;
        }
        catch{return null;}
    }
}
