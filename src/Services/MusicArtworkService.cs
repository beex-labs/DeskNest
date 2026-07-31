using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;

namespace BeeX.DeskNest;

internal static class MusicArtworkService
{
    static readonly HttpClient Http = CreateClient();
    public static string CacheDirectory => Path.Combine(BeeXPaths.DataDir, "artwork-cache");

    public static async Task<BitmapImage?> FindAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token = default)
    {
        title = Clean(title);
        artist = Clean(artist);
        album = Clean(album);
        if (string.IsNullOrWhiteSpace(title)) return null;
        Directory.CreateDirectory(CacheDirectory);
        var cachePath = CachePath(title, artist, album);
        if (File.Exists(cachePath))
        {
            var cached = LoadBitmap(cachePath);
            if (cached != null && cached.PixelWidth >= 420) return cached;
            try { File.Delete(cachePath); } catch { }
        }

        var url = await FindNetEaseCoverUrlAsync(title, artist, album, duration, token)
                  ?? await FindITunesCoverUrlAsync(title, artist, album, duration, token);
        if (string.IsNullOrWhiteSpace(url)) return null;
        var bytes = await DownloadAsync(url, token);
        if (bytes == null || bytes.Length < 4096) return null;
        await File.WriteAllBytesAsync(cachePath, bytes, token);
        var image = LoadBitmap(cachePath);
        if (image == null || image.PixelWidth < 300)
        {
            try { File.Delete(cachePath); } catch { }
            return null;
        }
        return image;
    }

    /// <summary>
    /// Loads artwork directly from a cover URL: normalizes to https with a moderate size parameter and
    /// caches to disk by URL hash, so a cache hit opens instantly and avoids re-downloading on every track change.
    /// </summary>
    public static async Task<BitmapImage?> LoadCoverFromUrlAsync(string url, CancellationToken token = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            Directory.CreateDirectory(CacheDirectory);
            var cacheFile = UrlCachePath(url);
            if (File.Exists(cacheFile))
            {
                var cached = LoadBitmap(cacheFile);
                if (cached != null) return cached;
                try { File.Delete(cacheFile); } catch { }
            }
            var u = SizedCoverUrl(url);
            var bytes = await DownloadAsync(u, token);
            if (bytes == null || bytes.Length < 2048) return null;
            await File.WriteAllBytesAsync(cacheFile, bytes, token);
            return LoadBitmap(cacheFile);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// <summary>Checks only the local URL cover cache without going online; used to instantly open a cached cover when switching tracks.</summary>
    public static BitmapImage? TryGetCachedCoverByUrl(string url)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(url)) return null;
            var f = UrlCachePath(url);
            return File.Exists(f) ? LoadBitmap(f) : null;
        }
        catch { return null; }
    }

    /// <summary>Caches already-obtained cover bytes to disk under the same URL key and decodes them, so switching back opens instantly.</summary>
    public static async Task<BitmapImage?> CacheCoverBytesAsync(string url, byte[] bytes, CancellationToken token = default)
    {
        try
        {
            if (bytes == null || bytes.Length < 2048) return null;
            Directory.CreateDirectory(CacheDirectory);
            var f = UrlCachePath(url);
            await File.WriteAllBytesAsync(f, bytes, token);
            return LoadBitmap(f);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    static string SizedCoverUrl(string url)
    {
        var u = url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "https://" + url[7..] : url;
        return u.Contains('?') ? u : u + "?param=512y512";
    }

    static string UrlCachePath(string url)
        => Path.Combine(CacheDirectory, "url_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SizedCoverUrl(url))))[..16] + ".jpg");

    static async Task<string?> FindNetEaseCoverUrlAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://music.163.com/api/search/get")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["s"] = $"{title} {artist}".Trim(),
                    ["type"] = "1",
                    ["limit"] = "10",
                    ["offset"] = "0"
                })
            };
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
            if (!json.RootElement.TryGetProperty("result", out var result) || !result.TryGetProperty("songs", out var songs)) return null;
            string? bestUrl = null;
            var bestScore = int.MinValue;
            foreach (var song in songs.EnumerateArray())
            {
                var name = song.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? "" : "";
                var singer = song.TryGetProperty("artists", out var artists) && artists.ValueKind == JsonValueKind.Array
                    ? string.Join(" / ", artists.EnumerateArray().Select(x => x.TryGetProperty("name", out var n) ? n.GetString() : "").Where(x => !string.IsNullOrWhiteSpace(x)))
                    : "";
                var songAlbum = song.TryGetProperty("album", out var albumNode) && albumNode.ValueKind == JsonValueKind.Object
                    ? albumNode.TryGetProperty("name", out var an) ? an.GetString() ?? "" : ""
                    : "";
                var score = MatchScore(title, artist, album, name, singer, songAlbum);
                if (duration > TimeSpan.FromSeconds(40) && song.TryGetProperty("duration", out var dur) && dur.TryGetInt64(out var ms))
                {
                    var diff = Math.Abs(duration.TotalMilliseconds - ms);
                    score += diff < 2500 ? 5 : diff < 8000 ? 2 : -3;
                }
                if (score <= bestScore) continue;
                var pic = "";
                if (albumNode.ValueKind == JsonValueKind.Object)
                {
                    if (albumNode.TryGetProperty("picUrl", out var picNode)) pic = picNode.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(pic) && albumNode.TryGetProperty("blurPicUrl", out var blurNode)) pic = blurNode.GetString() ?? "";
                }
                if (string.IsNullOrWhiteSpace(pic)) continue;
                bestScore = score;
                bestUrl = NormalizeCoverUrl(pic);
            }
            return bestScore >= (string.IsNullOrWhiteSpace(artist) ? 12 : 16) ? bestUrl : null;
        }
        catch { return null; }
    }

    static async Task<string?> FindITunesCoverUrlAsync(string title, string artist, string album, TimeSpan duration, CancellationToken token)
    {
        try
        {
            foreach (var query in SearchQueries(title, artist))
            {
                var url = "https://itunes.apple.com/search?entity=song&limit=12&term=" + Uri.EscapeDataString(query);
                using var response = await Http.GetAsync(url, token);
                response.EnsureSuccessStatusCode();
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array) continue;
                string? bestUrl = null;
                var bestScore = int.MinValue;
                foreach (var song in results.EnumerateArray())
                {
                    var name = song.TryGetProperty("trackName", out var n) ? n.GetString() ?? "" : "";
                    var singer = song.TryGetProperty("artistName", out var ar) ? ar.GetString() ?? "" : "";
                    var collection = song.TryGetProperty("collectionName", out var cn) ? cn.GetString() ?? "" : "";
                    var score = MatchScore(title, artist, album, name, singer, collection);
                    if (duration > TimeSpan.FromSeconds(40) && song.TryGetProperty("trackTimeMillis", out var dur) && dur.TryGetInt64(out var ms))
                    {
                        var diff = Math.Abs(duration.TotalMilliseconds - ms);
                        score += diff < 2500 ? 5 : diff < 9000 ? 2 : -3;
                    }
                    if (score <= bestScore) continue;
                    var art = song.TryGetProperty("artworkUrl100", out var artNode) ? artNode.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(art)) continue;
                    bestScore = score;
                    bestUrl = art.Replace("100x100bb", "1200x1200bb", StringComparison.OrdinalIgnoreCase)
                                 .Replace("100x100", "1200x1200", StringComparison.OrdinalIgnoreCase);
                }
                if (bestUrl != null && bestScore >= (string.IsNullOrWhiteSpace(artist) ? 12 : 16)) return bestUrl;
            }
        }
        catch { }
        return null;
    }

    static async Task<byte[]?> DownloadAsync(string url, CancellationToken token)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Referrer = new Uri("https://music.163.com/");
            using var response = await Http.SendAsync(request, token);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(token);
        }
        catch { return null; }
    }

    static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch { return null; }
    }

    static string NormalizeCoverUrl(string value)
    {
        var url = value.Replace("http://", "https://", StringComparison.OrdinalIgnoreCase);
        return url.Contains('?') ? url : url + "?param=1200y1200";
    }

    static int MatchScore(string expectedTitle, string expectedArtist, string expectedAlbum, string actualTitle, string actualArtist, string actualAlbum)
    {
        var et = Normalize(expectedTitle);
        var ea = Normalize(expectedArtist);
        var eb = Normalize(expectedAlbum);
        var at = Normalize(actualTitle);
        var aa = Normalize(actualArtist);
        var ab = Normalize(actualAlbum);
        if (string.IsNullOrWhiteSpace(et) || string.IsNullOrWhiteSpace(at)) return int.MinValue;
        var looseExpected = Normalize(LooseTitle(expectedTitle));
        var looseActual = Normalize(LooseTitle(actualTitle));
        var score = et == at || (!string.IsNullOrWhiteSpace(looseExpected)&&looseExpected==looseActual) ? 18 : at.Contains(et) || et.Contains(at) || (!string.IsNullOrWhiteSpace(looseExpected)&&(looseActual.Contains(looseExpected)||looseExpected.Contains(looseActual))) ? 12 : SharedPrefixScore(et, at);
        if (score < 8) return int.MinValue;
        if (!string.IsNullOrWhiteSpace(ea))
        {
            if (aa == ea) score += 12;
            else if (aa.Contains(ea) || ea.Contains(aa) || ArtistParts(actualArtist).Any(a => ArtistParts(expectedArtist).Any(e => a == e || a.Contains(e) || e.Contains(a)))) score += 8;
            else score -= 14;
        }
        if (!string.IsNullOrWhiteSpace(eb))
        {
            if (ab == eb) score += 6;
            else if (ab.Contains(eb) || eb.Contains(ab)) score += 3;
        }
        return score;
    }

    static int SharedPrefixScore(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var prefix = 0;
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        return prefix >= Math.Min(4, max) ? 7 : 0;
    }

    static List<string> ArtistParts(string value) => Regex.Split(value ?? "", @"[/、,，&＆+＋;；|｜\s]+")
        .Select(Normalize)
        .Where(x => x.Length > 0)
        .Distinct()
        .ToList();

    static string Clean(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
    static string Normalize(string value)
    {
        value = Clean(value).ToLowerInvariant();
        value = Regex.Replace(value, @"\((.*?)\)|（(.*?)）|\[(.*?)\]|【(.*?)】", "");
        value = Regex.Replace(value, @"\b(live|mv|伴奏|伴唱|karaoke|remaster(ed)?|explicit|vip)\b", "");
        return Regex.Replace(value, @"[^\p{L}\p{Nd}]+", "");
    }
    static string LooseTitle(string value)
    {
        value = Clean(value);
        value = Regex.Replace(value, @"\((.*?)\)|（(.*?)）|\[(.*?)\]|【(.*?)】", " ");
        value = Regex.Replace(value, @"[-_·•|｜]\s*(live|mv|remaster(ed)?|explicit|vip|伴奏|伴唱|karaoke)\s*$", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b(live|mv|伴奏|伴唱|karaoke|remaster(ed)?|explicit|vip)\b", " ", RegexOptions.IgnoreCase);
        return Clean(value);
    }

    static string CachePath(string title, string artist, string album)
    {
        var readable = string.Join(" - ", new[] { artist, title, album }.Where(x => !string.IsNullOrWhiteSpace(x)));
        foreach (var c in Path.GetInvalidFileNameChars()) readable = readable.Replace(c, '_');
        if (readable.Length > 80) readable = readable[..80];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("art-v2|" + Normalize(title) + "|" + Normalize(artist) + "|" + Normalize(album))))[..12];
        return Path.Combine(CacheDirectory, $"{readable}_{hash}.jpg");
    }

    static IEnumerable<string> SearchQueries(string title, string artist)
    {
        foreach (var q in new[] { $"{title} {artist}", $"{LooseTitle(title)} {artist}", title, LooseTitle(title) }.Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return q;
    }

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BeeXDeskNest/1.0");
        return client;
    }
}
