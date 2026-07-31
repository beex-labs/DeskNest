using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Services;

public class LyricsMatchingTests
{
    // ---- MatchScore ----

    [Fact]
    public void MatchScore_ExactTitleAndArtistMatch_ShouldReturnHighScore()
    {
        var score = LyricsMatching.MatchScore("晴天", "周杰倫", "晴天", "周杰倫");
        score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MatchScore_ExactTitleMatch_DifferentArtist_ShouldReturnLowerScore()
    {
        var exact = LyricsMatching.MatchScore("晴天", "周杰倫", "晴天", "周杰倫");
        var partial = LyricsMatching.MatchScore("晴天", "周杰倫", "晴天", "未知歌手");
        partial.Should().BeLessThan(exact);
    }

    [Fact]
    public void MatchScore_EmptyExpectedTitle_ShouldReturnMinValue()
    {
        var score = LyricsMatching.MatchScore("", "artist", "title", "artist");
        score.Should().Be(int.MinValue);
    }

    [Fact]
    public void MatchScore_EmptyActualTitle_ShouldReturnMinValue()
    {
        var score = LyricsMatching.MatchScore("title", "artist", "", "artist");
        score.Should().Be(int.MinValue);
    }

    [Fact]
    public void MatchScore_NullExpectedTitle_ShouldReturnMinValue()
    {
        var score = LyricsMatching.MatchScore("  ", "artist", "title", "artist");
        score.Should().Be(int.MinValue);
    }

    [Fact]
    public void MatchScore_SubstringTitleMatch_ShouldReturnMediumScore()
    {
        // "Sunny Day Remix" features "Sunny Day"
        var score = LyricsMatching.MatchScore("晴天", "artist", "晴天 Remix", "artist");
        score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MatchScore_CompletelyDifferentTitles_ShouldReturnMinValue()
    {
        var score = LyricsMatching.MatchScore("晴天", "artist", "稻香", "artist");
        score.Should().Be(int.MinValue);
    }

    [Fact]
    public void MatchScore_EnglishTitleMatch_ShouldWork()
    {
        var score = LyricsMatching.MatchScore("Shape of You", "Ed Sheeran", "Shape of You", "Ed Sheeran");
        score.Should().BeGreaterThan(0);
    }

    // ---- ArtistScore ----

    [Fact]
    public void ArtistScore_ExactMatch_ShouldReturn8()
    {
        var score = LyricsMatching.ArtistScore("周杰倫", "周杰倫");
        score.Should().Be(8);
    }

    [Fact]
    public void ArtistScore_EmptyExpected_ShouldReturn0()
    {
        var score = LyricsMatching.ArtistScore("", "artist");
        score.Should().Be(0);
    }

    [Fact]
    public void ArtistScore_EmptyActual_ShouldReturnNegative2()
    {
        var score = LyricsMatching.ArtistScore("artist", "");
        score.Should().Be(-2);
    }

    [Fact]
    public void ArtistScore_SubstringMatch_ShouldReturn5()
    {
        var score = LyricsMatching.ArtistScore("Ed Sheeran", "Ed Sheeran feat. Taylor");
        score.Should().Be(5);
    }

    [Fact]
    public void ArtistScore_CompletelyDifferent_ShouldReturnNegative8()
    {
        var score = LyricsMatching.ArtistScore("周杰倫", "陳奕迅");
        score.Should().Be(-8);
    }

    [Fact]
    public void ArtistScore_MultiPartArtist_ShouldMatchSharedPart()
    {
        // "A&B" splits into ["a", "b"], "C&B" splits into ["c", "b"] → shared "b"
        var score = LyricsMatching.ArtistScore("A&B", "C&B");
        score.Should().Be(5);
    }

    // ---- MetadataScore ----

    [Fact]
    public void MetadataScore_WithMatchingLrcTags_ShouldReturnHighScore()
    {
        var lrc = "[ti:晴天]\n[ar:周杰倫]\n[00:12.34]故事的小黄花";
        var score = LyricsMatching.MetadataScore("晴天", "周杰倫", "fallback", lrc);
        score.Should().BeGreaterThan(0);
    }

    [Fact]
    public void MetadataScore_NoTags_FallbackToFileName_ShouldWork()
    {
        var lrc = "[00:12.34]故事的小黄花";
        var score = LyricsMatching.MetadataScore("晴天", "周杰倫", "晴天", lrc);
        // Should use the fallback name "Sunny Day" for title matching
        score.Should().NotBe(int.MinValue);
    }

    [Fact]
    public void MetadataScore_CompleteMismatch_ShouldReturnNegative8()
    {
        var lrc = "[ti:稻香]\n[ar:陳奕迅]\n[00:12.34]歌词";
        var score = LyricsMatching.MetadataScore("晴天", "周杰倫", "稻香", lrc);
        score.Should().Be(-8);
    }

    // ---- DurationMatches ----

    [Fact]
    public void DurationMatches_NoExpectedDuration_ShouldReturnTrue()
    {
        var lines = new List<LyricLine> { new(TimeSpan.FromMinutes(3), "歌词") };
        LyricsMatching.DurationMatches(lines, null).Should().BeTrue();
    }

    [Fact]
    public void DurationMatches_ShortExpectedDuration_ShouldReturnTrue()
    {
        // expectedDuration < 45s → always true
        var lines = new List<LyricLine> { new(TimeSpan.FromMinutes(3), "歌词") };
        LyricsMatching.DurationMatches(lines, TimeSpan.FromSeconds(30)).Should().BeTrue();
    }

    [Fact]
    public void DurationMatches_EmptyLines_ShouldReturnTrue()
    {
        var lines = new List<LyricLine>();
        LyricsMatching.DurationMatches(lines, TimeSpan.FromMinutes(3)).Should().BeTrue();
    }

    [Fact]
    public void DurationMatches_WithinTolerance_ShouldReturnTrue()
    {
        // lyricEnd = 200s, expected = 210s, diff = 10s, tolerance = max(45, 210*0.15=31.5) = 31.5
        var lines = new List<LyricLine> { new(TimeSpan.FromSeconds(200), "歌词") };
        LyricsMatching.DurationMatches(lines, TimeSpan.FromSeconds(210)).Should().BeTrue();
    }

    [Fact]
    public void DurationMatches_OutsideTolerance_ShouldReturnFalse()
    {
        // lyricEnd = 60s, expected = 300s, diff = 240s, tolerance = max(45, 300*0.15=45) = 45
        var lines = new List<LyricLine> { new(TimeSpan.FromSeconds(60), "歌词") };
        LyricsMatching.DurationMatches(lines, TimeSpan.FromSeconds(300)).Should().BeFalse();
    }

    [Fact]
    public void DurationMatches_LyricEndLessThan20s_ShouldReturnTrue()
    {
        var lines = new List<LyricLine> { new(TimeSpan.FromSeconds(15), "歌词") };
        LyricsMatching.DurationMatches(lines, TimeSpan.FromMinutes(5)).Should().BeTrue();
    }

    // ---- LrcIdentityMatches ----

    [Fact]
    public void LrcIdentityMatches_NoTags_ShouldReturnTrue()
    {
        var lrc = "[00:12.34]歌词内容";
        LyricsMatching.LrcIdentityMatches(lrc, "title", "artist").Should().BeTrue();
    }

    [Fact]
    public void LrcIdentityMatches_MatchingTags_ShouldReturnTrue()
    {
        var lrc = "[ti:晴天]\n[ar:周杰倫]\n[00:12.34]歌词";
        LyricsMatching.LrcIdentityMatches(lrc, "晴天", "周杰倫").Should().BeTrue();
    }

    [Fact]
    public void LrcIdentityMatches_MismatchingTags_ShouldReturnFalse()
    {
        var lrc = "[ti:稻香]\n[ar:陳奕迅]\n[00:12.34]歌词";
        LyricsMatching.LrcIdentityMatches(lrc, "晴天", "周杰倫").Should().BeFalse();
    }
}
