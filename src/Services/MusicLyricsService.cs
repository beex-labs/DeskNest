using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeeX.DeskNest.LyricsProviders;

namespace BeeX.DeskNest;

internal sealed record LyricLine(TimeSpan Time, string Text);
internal sealed record LyricsDocument(string Title, string Artist, string Provider, string CachePath, IReadOnlyList<LyricLine> Lines);

internal static partial class MusicLyricsService
{
    internal static readonly HttpClient Http = CreateClient();
    public static string CacheDirectory => Path.Combine(BeeXPaths.DataDir, "lyrics-cache");
    public static bool ClearCache(string title, string artist)
    {
        try
        {
            var path = CachePath(LyricsMatching.Clean(title), LyricsMatching.Clean(artist));
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Returns all local player lyrics directories that should be watched (for FileSystemWatcher).</summary>
    public static IReadOnlyList<string> GetWatchDirectories()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(local,"Netease","CloudMusic","webdata","lyric"),
            Path.Combine(local,"Netease","CloudMusic","Cache","Lyric"),
            Path.Combine(local,"Netease","CloudMusic","Lyrics"),
            Path.Combine(appData,"Netease","CloudMusic"),
            Path.Combine(appData,"Tencent","QQMusic","Lyric"),
            Path.Combine(local,"Tencent","QQMusic","Lyric"),
            Path.Combine(music,"QQMusic"),
            Path.Combine(documents,"Tencent Files","QQMusic"),
            Path.Combine(appData,"KuGou8","Lyric"),
            Path.Combine(music,"KuGou"),
            Path.Combine(appData,"Apple Computer","iTunes"),
            Path.Combine(local,"Packages","AppleInc.AppleMusicWin_nzyj5cx40ttqa"),
            Path.Combine(local,"Packages","AppleInc.iTunes_nzyj5cx40ttqa"),
            Path.Combine(music,"iTunes"),
            Path.Combine(music,"Apple Music"),
            Path.Combine(local,"SodaMusic","Lyric"),
            Path.Combine(appData,"SodaMusic","Lyric"),
            Path.Combine(music,"SodaMusic"),
            Path.Combine(appData,"Bytedance","SodaMusic","Lyric"),
            Path.Combine(appData,"KuwoMusic","Lyric"),
            Path.Combine(music,"KuwoMusic")
        };
        var packagesDir = Path.Combine(local, "Packages");
        if (Directory.Exists(packagesDir))
        {
            foreach (var prefix in new[] { "Bytedance.SodaMusic", "CloudMusic.1.0", "TencentTechnolog", "Kugou", "Kuwo" })
            {
                try
                {
                    foreach (var dir in Directory.EnumerateDirectories(packagesDir, prefix + "*"))
                    {
                        foreach (var sub in new[] { Path.Combine(dir, "LocalState", "Lyric"), Path.Combine(dir, "LocalCache", "Roaming"), Path.Combine(dir, "LocalState") })
                            if (Directory.Exists(sub)) roots.Add(sub);
                    }
                }
                catch { }
            }
        }
        return roots.Where(Directory.Exists).ToList();
    }

    /// <summary>Checks whether a local .lrc file matches the currently playing song; if so, returns the parsed lyrics text.</summary>
    public static LyricsDocument? TryLoadFromLocalFile(string lrcPath, string title, string artist, IEnumerable<string>? playerFolders = null, TimeSpan? expectedDuration = null)
    {
        try
        {
            title = LyricsMatching.Clean(title); artist = LyricsMatching.Clean(artist);
            if (string.IsNullOrWhiteSpace(title) || !File.Exists(lrcPath)) return null;
            var text = File.ReadAllText(lrcPath);
            if (text.Length > 2_000_000) return null;
            var parsed = LyricsParser.Parse(text);
            if (parsed.Count == 0) return null;
            var score = LyricsMatching.MetadataScore(title, artist, Path.GetFileNameWithoutExtension(lrcPath), text);
            var expectedTitle = LyricsMatching.Normalize(title);
            var fileNameMatches = LyricsMatching.Normalize(Path.GetFileNameWithoutExtension(lrcPath)).Contains(expectedTitle);
            if (score < 10 && !fileNameMatches) return null;
            if (!LyricsMatching.DurationMatches(parsed, expectedDuration)) return null;
            var cachePath = CachePath(title, artist);
            var tagLine = $"[ti:{title}]\n[ar:{artist}]\n[by:BeeX DeskNest · 播放器本機歌詞]\n";
            var content = tagLine + text.Trim() + "\n";
            try { File.WriteAllText(cachePath, content, new UTF8Encoding(false)); } catch { }
            return new(title, artist, "播放器本機歌詞", cachePath, LyricsParser.Parse(content));
        }
        catch { return null; }
    }

    public static async Task<LyricsDocument?> FindAsync(string title, string artist, IEnumerable<string>? playerFolders = null, TimeSpan? expectedDuration = null, CancellationToken cancellationToken = default, bool skipCache = false)
    {
        title = LyricsMatching.Clean(title); artist = LyricsMatching.Clean(artist);
        if (string.IsNullOrWhiteSpace(title)) return null;
        Directory.CreateDirectory(CacheDirectory);
        var cachePath = CachePath(title, artist);
        var log=new System.Text.StringBuilder();
        log.AppendLine($"[{DateTime.Now:HH:mm:ss}] 搜索: {title} - {artist} dur={expectedDuration?.TotalSeconds:0}s");
        if (!skipCache && File.Exists(cachePath))
        {
            var cached = await File.ReadAllTextAsync(cachePath, cancellationToken);
            // Old cache may lack the [ti:] tag and cannot verify identity; delete it and search again
            if (string.IsNullOrWhiteSpace(LyricsParser.ReadLrcTag(cached, "ti"))) { try { File.Delete(cachePath); } catch { } log.AppendLine("  缓存无[ti:]标签,已删除"); }
            else
            {
                var parsed = LyricsParser.Parse(cached);
                if (parsed.Count > 0 && LyricsMatching.LrcIdentityMatches(cached, title, artist) && LyricsMatching.DurationMatches(parsed, expectedDuration)){ log.AppendLine($"  命中缓存"); WriteLog(log); return new(title, artist, LyricsParser.ReadProvider(cached) ?? "本機快取", cachePath, parsed);}
                try { File.Delete(cachePath); } catch { } log.AppendLine("  缓存验证失败,已删除重新搜索");
            }
        }

        // Parallel search: original + simplified/traditional variants, all sources tried at once
        var tasks = new List<Task<(string Provider, string Lrc)?>>();
        foreach(var (vt,va,vName) in ChineseConversion.LrcVariants(title,artist))
        {
            tasks.Add(LocalPlayerLyricsProvider.TryAsync(vt,va,playerFolders,cancellationToken));
            tasks.Add(NetEaseLyricsProvider.TryAsync(vt,va,cancellationToken));
            tasks.Add(QQMusicLyricsProvider.TryAsync(vt,va,cancellationToken));
            tasks.Add(KugouLyricsProvider.TryAsync(vt,va,cancellationToken));
            tasks.Add(LrcLibLyricsProvider.TryAsync(vt,va,expectedDuration,cancellationToken));
        }
        var remaining = new List<Task<(string, string)?>>(tasks);
        (string Provider, string Lrc)? durationFallback = null;
        while (remaining.Count > 0)
        {
            var completed = await Task.WhenAny(remaining);
            remaining.Remove(completed);
            (string Provider, string Lrc)? result;
            try{result=await completed;}catch(Exception ex){log.AppendLine($"  源异常: {ex.Message}");continue;}
            if (result == null){log.AppendLine("  源返回null");continue;}
            log.AppendLine($"  {result.Value.Provider}: {result.Value.Lrc.Length} 字符");
            var parsed = LyricsParser.Parse(result.Value.Lrc);
            if (parsed.Count == 0){log.AppendLine($"    解析后0行,跳过");continue;}
            if (!LyricsMatching.DurationMatches(parsed, expectedDuration)){durationFallback ??= result.Value;log.AppendLine($"    时长不匹配(lastLine={parsed[^1].Time.TotalSeconds:0}s),暂存为兜底");continue;}
            log.AppendLine($"    ✓ 采用 (共{parsed.Count}行)");WriteLog(log);
            var tagLine = $"[ti:{title}]\n[ar:{artist}]\n[by:BeeX DeskNest · {result.Value.Item1}]\n";
            var content = tagLine + result.Value.Item2.Trim() + "\n";
            await File.WriteAllTextAsync(cachePath, content, new UTF8Encoding(false), cancellationToken);
            SaveVariantCaches(title,artist,content,cancellationToken);
            return new(title, artist, result.Value.Item1, cachePath, LyricsParser.Parse(content));
        }
        if (durationFallback.HasValue)
        {
            log.AppendLine($"  兜底采用(duration不匹配): {durationFallback.Value.Provider}");WriteLog(log);
            var tagLine = $"[ti:{title}]\n[ar:{artist}]\n[by:BeeX DeskNest · {durationFallback.Value.Provider}]\n";
            var content = tagLine + durationFallback.Value.Lrc.Trim() + "\n";
            await File.WriteAllTextAsync(cachePath, content, new UTF8Encoding(false), cancellationToken);
            SaveVariantCaches(title,artist,content,cancellationToken);
            return new(title, artist, durationFallback.Value.Provider, cachePath, LyricsParser.Parse(content));
        }
        log.AppendLine("  ✗ 未找到任何歌词");WriteLog(log);
        return null;
    }


    static void WriteLog(System.Text.StringBuilder log)
    {
        try
        {
            var logDir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"BeeX","DeskNest");
            Directory.CreateDirectory(logDir);
            File.AppendAllText(Path.Combine(logDir,"lyrics-search.log"),log.ToString());
        }
        catch{}
    }

    static void SaveVariantCaches(string title,string artist,string content,CancellationToken cancellationToken)
    {
        foreach(var (vt,va,vName) in ChineseConversion.LrcVariants(title,artist))
        {
            if(vt==title&&va==artist)continue; // original already saved
            var tagLine=$"[ti:{vt}]\n[ar:{va}]\n[by:BeeX DeskNest · 变体缓存]\n";
            var variantContent=tagLine+content[(content.IndexOf('\n')+1)..]; // replace the tag line
            var path=CachePath(vt,va);
            if(File.Exists(path))continue;
            try{File.WriteAllText(path,variantContent,new UTF8Encoding(false));}catch{}
        }
    }
    static string CachePath(string title, string artist)
    {
        var readable = string.Join(" - ", new[] { artist, title }.Where(x => !string.IsNullOrWhiteSpace(x)));
        foreach (var c in Path.GetInvalidFileNameChars()) readable = readable.Replace(c, '_');
        if (readable.Length > 72) readable = readable[..72];
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("v2|" + LyricsMatching.Normalize(title) + "|" + LyricsMatching.Normalize(artist))))[..10];
        return Path.Combine(CacheDirectory, $"{readable} [{hash}].lrc");
    }

    internal static string Clean(string value) => LyricsMatching.Clean(value);

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 BeeX-DeskNest/1.0");
        return client;
    }
}
