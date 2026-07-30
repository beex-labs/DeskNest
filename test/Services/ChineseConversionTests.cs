using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Services;

public class ChineseConversionTests
{
    // ---- ToSimplified ----

    [Fact]
    public void ToSimplified_TraditionalChinese_ShouldConvertToSimplified()
    {
        ChineseConversion.ToSimplified("愛").Should().Be("爱");
    }

    [Fact]
    public void ToSimplified_Dragon_ShouldConvert()
    {
        ChineseConversion.ToSimplified("龍").Should().Be("龙");
    }

    [Fact]
    public void ToSimplified_AlreadySimplified_ShouldRemainSame()
    {
        ChineseConversion.ToSimplified("爱").Should().Be("爱");
    }

    [Fact]
    public void ToSimplified_EnglishText_ShouldRemainUnchanged()
    {
        ChineseConversion.ToSimplified("Hello World").Should().Be("Hello World");
    }

    [Fact]
    public void ToSimplified_Numbers_ShouldRemainUnchanged()
    {
        ChineseConversion.ToSimplified("12345").Should().Be("12345");
    }

    [Fact]
    public void ToSimplified_EmptyString_ShouldReturnEmpty()
    {
        ChineseConversion.ToSimplified("").Should().BeEmpty();
    }

    [Fact]
    public void ToSimplified_Null_ShouldReturnNull()
    {
        ChineseConversion.ToSimplified(null!).Should().BeNull();
    }

    [Fact]
    public void ToSimplified_MixedText_ShouldConvertOnlyChinese()
    {
        var result = ChineseConversion.ToSimplified("Hello 愛 World");
        result.Should().Contain("爱");
        result.Should().Contain("Hello");
        result.Should().Contain("World");
    }

    // ---- ToTraditional ----

    [Fact]
    public void ToTraditional_SimplifiedChinese_ShouldConvertToTraditional()
    {
        ChineseConversion.ToTraditional("爱").Should().Be("愛");
    }

    [Fact]
    public void ToTraditional_Dragon_ShouldConvert()
    {
        ChineseConversion.ToTraditional("龙").Should().Be("龍");
    }

    [Fact]
    public void ToTraditional_AlreadyTraditional_ShouldRemainSame()
    {
        ChineseConversion.ToTraditional("愛").Should().Be("愛");
    }

    [Fact]
    public void ToTraditional_EnglishText_ShouldRemainUnchanged()
    {
        ChineseConversion.ToTraditional("Hello World").Should().Be("Hello World");
    }

    [Fact]
    public void ToTraditional_EmptyString_ShouldReturnEmpty()
    {
        ChineseConversion.ToTraditional("").Should().BeEmpty();
    }

    [Fact]
    public void ToTraditional_Null_ShouldReturnNull()
    {
        ChineseConversion.ToTraditional(null!).Should().BeNull();
    }

    // ---- ContainsChinese ----

    [Fact]
    public void ContainsChinese_ChineseText_ShouldReturnTrue()
    {
        ChineseConversion.ContainsChinese("中文").Should().BeTrue();
    }

    [Fact]
    public void ContainsChinese_EnglishOnly_ShouldReturnFalse()
    {
        ChineseConversion.ContainsChinese("English").Should().BeFalse();
    }

    [Fact]
    public void ContainsChinese_NumbersOnly_ShouldReturnFalse()
    {
        ChineseConversion.ContainsChinese("12345").Should().BeFalse();
    }

    [Fact]
    public void ContainsChinese_MixedText_ShouldReturnTrue()
    {
        ChineseConversion.ContainsChinese("Hello 世界").Should().BeTrue();
    }

    [Fact]
    public void ContainsChinese_EmptyString_ShouldReturnFalse()
    {
        ChineseConversion.ContainsChinese("").Should().BeFalse();
    }

    [Fact]
    public void ContainsChinese_SpecialCharacters_ShouldReturnFalse()
    {
        ChineseConversion.ContainsChinese("!@#$%^&*()").Should().BeFalse();
    }

    // ---- Roundtrip: Simplified → Traditional → Simplified ----

    [Fact]
    public void Roundtrip_SimplifiedToTraditionalAndBack_ShouldReturnOriginal()
    {
        var original = "爱";
        var traditional = ChineseConversion.ToTraditional(original);
        var backToSimplified = ChineseConversion.ToSimplified(traditional);
        backToSimplified.Should().Be(original);
    }
}
