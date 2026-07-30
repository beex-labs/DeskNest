using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Services;

public class StateSanitizationTests
{
    // ---- Finite: 正常值返回原值 ----

    [Fact]
    public void Finite_NormalValue_ReturnsOriginal()
    {
        DeskNestService.Finite(0.5, 0.5, 0, 1).Should().Be(0.5);
    }

    [Fact]
    public void Finite_MinBoundary_ReturnsMin()
    {
        DeskNestService.Finite(0, 0.5, 0, 1).Should().Be(0);
    }

    [Fact]
    public void Finite_MaxBoundary_ReturnsMax()
    {
        DeskNestService.Finite(1, 0.5, 0, 1).Should().Be(1);
    }

    [Fact]
    public void Finite_ValueBelowMin_ClampedToMin()
    {
        DeskNestService.Finite(-0.5, 0.5, 0, 1).Should().Be(0);
    }

    [Fact]
    public void Finite_ValueAboveMax_ClampedToMax()
    {
        DeskNestService.Finite(1.5, 0.5, 0, 1).Should().Be(1);
    }

    // ---- Finite: NaN 返回回退值 ----

    [Fact]
    public void Finite_NaN_ReturnsFallback()
    {
        DeskNestService.Finite(double.NaN, 0.5, 0, 1).Should().Be(0.5);
    }

    // ---- Finite: Infinity 返回回退值 ----

    [Fact]
    public void Finite_PositiveInfinity_ReturnsFallback()
    {
        DeskNestService.Finite(double.PositiveInfinity, 14, 10, 36).Should().Be(14);
    }

    [Fact]
    public void Finite_NegativeInfinity_ReturnsFallback()
    {
        DeskNestService.Finite(double.NegativeInfinity, 14, 10, 36).Should().Be(14);
    }

    // ---- Finite: 不同回退值和范围 ----

    [Fact]
    public void Finite_FontSizeRange_ClampedCorrectly()
    {
        // GlobalFontSize range: 10-36, fallback 14
        DeskNestService.Finite(8, 14, 10, 36).Should().Be(10);
        DeskNestService.Finite(40, 14, 10, 36).Should().Be(36);
        DeskNestService.Finite(20, 14, 10, 36).Should().Be(20);
    }

    [Fact]
    public void Finite_CornerRadiusRange_ClampedCorrectly()
    {
        // CornerRadius range: 0-48, fallback 18
        DeskNestService.Finite(-5, 18, 0, 48).Should().Be(0);
        DeskNestService.Finite(50, 18, 0, 48).Should().Be(48);
        DeskNestService.Finite(24, 18, 0, 48).Should().Be(24);
    }

    [Fact]
    public void Finite_IconSizeRange_ClampedCorrectly()
    {
        // IconSize range: 12-96, fallback 30
        DeskNestService.Finite(10, 30, 12, 96).Should().Be(12);
        DeskNestService.Finite(100, 30, 12, 96).Should().Be(96);
    }

    [Fact]
    public void Finite_NegativeValue_WithZeroMin_ClampedToZero()
    {
        DeskNestService.Finite(-100, 0.5, 0, 1).Should().Be(0);
    }

    [Fact]
    public void Finite_ZeroFallback_ForNaN()
    {
        DeskNestService.Finite(double.NaN, 0, 0, 1).Should().Be(0);
    }

    [Fact]
    public void Finite_LargeRange_NormalValuePassesThrough()
    {
        // nest.Left range: -20000 to 20000, fallback 80
        DeskNestService.Finite(500, 80, -20000, 20000).Should().Be(500);
        DeskNestService.Finite(-15000, 80, -20000, 20000).Should().Be(-15000);
    }
}
