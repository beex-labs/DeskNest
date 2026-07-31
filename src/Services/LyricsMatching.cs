using System.Text.RegularExpressions;

namespace BeeX.DeskNest;

/// <summary>Lyrics match scoring algorithm (pure functions, suitable for unit testing).</summary>
internal static partial class LyricsMatching
{
    public static int MatchScore(string expectedTitle, string expectedArtist, string actualTitle, string actualArtist)
    {
        var et = Normalize(expectedTitle); var ea = Normalize(expectedArtist); var at = Normalize(actualTitle); var aa = Normalize(actualArtist);
        if (string.IsNullOrWhiteSpace(et) || string.IsNullOrWhiteSpace(at)) return int.MinValue;
        var looseExpected = Normalize(LooseTitle(expectedTitle));
        var looseActual = Normalize(LooseTitle(actualTitle));
        var score = et == at || (!string.IsNullOrWhiteSpace(looseExpected)&&looseExpected==looseActual) ? 12 : at.Contains(et) || et.Contains(at) || (!string.IsNullOrWhiteSpace(looseExpected)&&(looseActual.Contains(looseExpected)||looseExpected.Contains(looseActual))) ? 8 : SharedPrefixScore(et, at);
        if (score < 6) return int.MinValue;
        score += ArtistScore(expectedArtist, actualArtist);
        return score;
    }

    public static int MetadataScore(string expectedTitle, string expectedArtist, string fallbackName, string lrc)
    {
        var tagTitle = LyricsParser.ReadLrcTag(lrc, "ti");
        var tagArtist = LyricsParser.ReadLrcTag(lrc, "ar");
        var title = string.IsNullOrWhiteSpace(tagTitle) ? fallbackName : tagTitle;
        var score = MatchScore(expectedTitle, expectedArtist, title, tagArtist);
        if (score == int.MinValue) score = MatchScore(expectedTitle, expectedArtist, fallbackName, tagArtist);
        return score == int.MinValue ? -8 : score;
    }

    public static bool LrcIdentityMatches(string lrc, string title, string artist)
    {
        var tagTitle = LyricsParser.ReadLrcTag(lrc, "ti");
        var tagArtist = LyricsParser.ReadLrcTag(lrc, "ar");
        if (string.IsNullOrWhiteSpace(tagTitle) && string.IsNullOrWhiteSpace(tagArtist)) return true;
        return MatchScore(title, artist, string.IsNullOrWhiteSpace(tagTitle) ? title : tagTitle, tagArtist) >= 9;
    }

    public static bool DurationMatches(IReadOnlyList<LyricLine> lines, TimeSpan? expectedDuration)
    {
        if (!expectedDuration.HasValue || expectedDuration.Value < TimeSpan.FromSeconds(45) || lines.Count == 0) return true;
        var lyricEnd = lines[^1].Time;
        if (lyricEnd < TimeSpan.FromSeconds(20)) return true;
        var diff = Math.Abs((expectedDuration.Value - lyricEnd).TotalSeconds);
        var tolerance = Math.Max(45, expectedDuration.Value.TotalSeconds * 0.15);
        return diff <= tolerance;
    }

    static int SharedPrefixScore(string a, string b)
    {
        var max = Math.Min(a.Length, b.Length);
        var prefix = 0;
        while (prefix < max && a[prefix] == b[prefix]) prefix++;
        return prefix >= Math.Min(4, max) ? 5 : 0;
    }

    internal static int ArtistScore(string expectedArtist, string actualArtist)
    {
        var ea = Normalize(expectedArtist); var aa = Normalize(actualArtist);
        if (string.IsNullOrWhiteSpace(ea)) return 0;
        if (string.IsNullOrWhiteSpace(aa)) return -2;
        if (ea == aa) return 8;
        if (aa.Contains(ea) || ea.Contains(aa)) return 5;
        var expectedParts = ArtistParts(expectedArtist);
        var actualParts = ArtistParts(actualArtist);
        if (expectedParts.Count > 0 && actualParts.Any(a => expectedParts.Any(e => a == e || a.Contains(e) || e.Contains(a)))) return 5;
        return -8;
    }

    static List<string> ArtistParts(string value) => Regex.Split(value ?? "", @"[/、,，&＆+＋;；|｜\s]+")
        .Select(Normalize)
        .Where(x => x.Length > 0)
        .Distinct()
        .ToList();

    public static string Clean(string value) => Regex.Replace(value ?? "", @"\s+", " ").Trim();
    public static string Normalize(string value) => Regex.Replace(Clean(value).ToLowerInvariant(), @"[^\p{L}\p{N}]", "");
    public static string LooseTitle(string value)
    {
        value = Clean(value);
        value = Regex.Replace(value, @"\((.*?)\)|（(.*?)）|\[(.*?)\]|【(.*?)】", " ");
        value = Regex.Replace(value, @"[-_·•|｜]\s*(live|mv|remaster(ed)?|explicit|vip|伴奏|伴唱|karaoke)\s*$", " ", RegexOptions.IgnoreCase);
        value = Regex.Replace(value, @"\b(live|mv|remaster(ed)?|explicit|伴奏|伴唱|karaoke|vip)\b", " ", RegexOptions.IgnoreCase);
        return Clean(value);
    }
    public static IEnumerable<string> SearchQueries(string title, string artist)
    {
        foreach (var q in new[] { $"{title} {artist}", $"{LooseTitle(title)} {artist}", title, LooseTitle(title) }.Select(Clean).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return q;
    }
}
