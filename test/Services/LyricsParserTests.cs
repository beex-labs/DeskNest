using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Services;

public class LyricsParserTests
{
    // ---- Parse: Standard LRC Timestamps ----

    [Fact]
    public void Parse_StandardTimestamp_ShouldParseCorrectly()
    {
        var lrc = "[00:12.34]故事的小黄花";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(1);
        lines[0].Time.Should().BeCloseTo(TimeSpan.FromSeconds(12.34), TimeSpan.FromMilliseconds(10));
        lines[0].Text.Should().Be("故事的小黄花");
    }

    [Fact]
    public void Parse_MultipleTimestampsOnSameLine_ShouldCreateMultipleEntries()
    {
        var lrc = "[00:12.34][00:45.67]副歌歌词";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(2);
        lines[0].Time.Should().BeCloseTo(TimeSpan.FromSeconds(12.34), TimeSpan.FromMilliseconds(10));
        lines[1].Time.Should().BeCloseTo(TimeSpan.FromSeconds(45.67), TimeSpan.FromMilliseconds(10));
        lines[0].Text.Should().Be("副歌歌词");
        lines[1].Text.Should().Be("副歌歌词");
    }

    [Fact]
    public void Parse_EmptyString_ShouldReturnEmptyList()
    {
        var lines = LyricsParser.Parse("");
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyLines_ShouldBeSkipped()
    {
        var lrc = "[00:12.34]第一句\n\n\n[00:25.00]第二句";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_NoTimestamp_ShouldBeSkipped()
    {
        var lrc = "这是纯文本没有标签";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_TimestampOnlyNoText_ShouldBeSkipped()
    {
        var lrc = "[00:12.34]";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().BeEmpty();
    }

    [Fact]
    public void Parse_MinutesOver60_ShouldParseCorrectly()
    {
        var lrc = "[120:00.00]超长时间";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(1);
        lines[0].Time.Should().Be(TimeSpan.FromMinutes(120));
    }

    [Fact]
    public void Parse_MillisecondsZero_ShouldParseCorrectly()
    {
        var lrc = "[00:30.00]整秒时间";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(1);
        lines[0].Time.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void Parse_ResultShouldBeOrderedByTime()
    {
        var lrc = "[01:00.00]第二句\n[00:30.00]第一句\n[02:00.00]第三句";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(3);
        lines[0].Time.Should().BeLessThan(lines[1].Time);
        lines[1].Time.Should().BeLessThan(lines[2].Time);
    }

    [Fact]
    public void Parse_WindowsLineEndings_ShouldHandleCorrectly()
    {
        var lrc = "[00:10.00]第一句\r\n[00:20.00]第二句\r\n";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(2);
    }

    [Fact]
    public void Parse_TagLinesAreSkipped()
    {
        // [ti:] and [ar:] lines have no text after timestamp removal → skipped
        var lrc = "[ti:晴天]\n[ar:周杰倫]\n[00:12.34]故事的小黄花";
        var lines = LyricsParser.Parse(lrc);
        lines.Should().HaveCount(1);
        lines[0].Text.Should().Be("故事的小黄花");
    }

    // ---- ReadLrcTag ----

    [Fact]
    public void ReadLrcTag_TitleTag_ShouldReturnValue()
    {
        var lrc = "[ti:晴天]\n[ar:周杰倫]\n[00:12.34]歌词";
        LyricsParser.ReadLrcTag(lrc, "ti").Should().Be("晴天");
    }

    [Fact]
    public void ReadLrcTag_ArtistTag_ShouldReturnValue()
    {
        var lrc = "[ti:晴天]\n[ar:周杰倫]\n[00:12.34]歌词";
        LyricsParser.ReadLrcTag(lrc, "ar").Should().Be("周杰倫");
    }

    [Fact]
    public void ReadLrcTag_NonExistentTag_ShouldReturnEmpty()
    {
        var lrc = "[ti:晴天]\n[00:12.34]歌词";
        LyricsParser.ReadLrcTag(lrc, "ar").Should().BeEmpty();
    }

    [Fact]
    public void ReadLrcTag_CaseInsensitive_ShouldWork()
    {
        var lrc = "[TI:晴天]\n[00:12.34]歌词";
        LyricsParser.ReadLrcTag(lrc, "ti").Should().Be("晴天");
    }

    [Fact]
    public void ReadLrcTag_ByTag_ShouldReturnProvider()
    {
        var lrc = "[by:BeeX DeskNest · 酷狗音樂]\n[00:12.34]歌词";
        LyricsParser.ReadLrcTag(lrc, "by").Should().Be("BeeX DeskNest · 酷狗音樂");
    }
}
