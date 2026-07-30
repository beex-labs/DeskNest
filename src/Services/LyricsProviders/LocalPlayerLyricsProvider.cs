using System.IO;
using BeeX.DeskNest;

namespace BeeX.DeskNest.LyricsProviders;

/// <summary>本地播放器歌词目录扫描</summary>
internal static class LocalPlayerLyricsProvider
{
    public static async Task<(string Provider, string Lrc)?> TryAsync(string title, string artist, IEnumerable<string>? playerFolders, CancellationToken token)
    {
        try
        {
            var roots = GetLocalPlayerRoots(playerFolders);
            return await Task.Run<(string Provider,string Lrc)?>(() =>
            {
                var expectedTitle=LyricsMatching.Normalize(title);string? bestText=null;var bestScore=int.MinValue;var examined=0;
                foreach(var root in roots.Where(Directory.Exists))
                {
                    IEnumerable<string> files;
                    try{files=Directory.EnumerateFiles(root,"*.lrc",SearchOption.AllDirectories);}catch{continue;}
                    try
                    {
                        foreach(var file in files)
                        {
                            token.ThrowIfCancellationRequested();if(++examined>1800)break;
                            var name=LyricsMatching.Normalize(Path.GetFileNameWithoutExtension(file));var fileNameMatches=name.Contains(expectedTitle);var score=fileNameMatches?8:0;
                            string text;try{text=File.ReadAllText(file);}catch{continue;}if(text.Length>2_000_000)continue;
                            score+=LyricsMatching.MetadataScore(title,artist,Path.GetFileNameWithoutExtension(file),text);
                            var sample=LyricsMatching.Normalize(text.Length>18000?text[..18000]:text);if(sample.Contains(expectedTitle))score+=5;
                            if(score>bestScore&&LyricsParser.Parse(text).Count>0&&(fileNameMatches||score>=10)){bestScore=score;bestText=text;if(score>=16)break;}
                        }
                    }catch{}
                    if(bestScore>=16||examined>1800)break;
                }
                return bestScore>=10&&bestText!=null?("播放器本機歌詞",bestText):null;
            },token);
        }
        catch(OperationCanceledException){throw;}
        catch{return null;}
    }

    static HashSet<string> GetLocalPlayerRoots(IEnumerable<string>? playerFolders)
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
        if (playerFolders != null) foreach (var folder in playerFolders) if (!string.IsNullOrWhiteSpace(folder)) roots.Add(folder);
        return roots;
    }
}
