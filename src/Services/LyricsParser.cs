using System.Globalization;
using System.Text.RegularExpressions;

namespace BeeX.DeskNest;

/// <summary>LRC 歌词格式解析</summary>
internal static partial class LyricsParser
{
    /// <summary>解析 LRC 文本为带时间轴的歌词行列表</summary>
    public static IReadOnlyList<LyricLine> Parse(string lrc)
    {
        var lines = new List<LyricLine>();
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var matches = TimestampRegex().Matches(raw);
            if (matches.Count == 0) continue;
            var text = TimestampRegex().Replace(raw, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;
            foreach (Match match in matches)
            {
                if (!int.TryParse(match.Groups[1].Value, out var minute) ||
                    !double.TryParse(match.Groups[2].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var second)) continue;
                lines.Add(new(TimeSpan.FromMinutes(minute) + TimeSpan.FromSeconds(second), text));
            }
        }
        return lines.OrderBy(x => x.Time).ToList();
    }

    /// <summary>读取 LRC 标签值（如 [ti:xxx] 中的 xxx）</summary>
    public static string ReadLrcTag(string lrc, string tag)
    {
        var match = Regex.Match(lrc, @"\[" + Regex.Escape(tag) + @":([^\]]+)\]", RegexOptions.IgnoreCase);
        return match.Success ? MusicLyricsService.Clean(match.Groups[1].Value) : "";
    }

    /// <summary>从缓存 LRC 中提取提供方信息</summary>
    public static string? ReadProvider(string lrc)
    {
        var match = ProviderRegex().Match(lrc);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]")]
    private static partial Regex TimestampRegex();
    [GeneratedRegex(@"\[by:BeeX DeskNest · ([^\]]+)\]")]
    private static partial Regex ProviderRegex();
}
