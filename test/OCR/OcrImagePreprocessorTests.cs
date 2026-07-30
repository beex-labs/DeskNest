using System.Drawing;
using System.Drawing.Imaging;
using BeeX.OCR;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.OCR;

public class OcrImagePreprocessorTests
{
    // ---- 辅助方法 ----

    private static Bitmap CreateSolidBitmap(int width, int height, Color color)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(color);
        return bmp;
    }

    private static Bitmap CreateGradientBitmap(int width, int height, Color from, Color to)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        for (int x = 0; x < width; x++)
        {
            float t = width > 1 ? x / (float)(width - 1) : 0;
            var color = Color.FromArgb(
                255,
                (int)(from.R + (to.R - from.R) * t),
                (int)(from.G + (to.G - from.G) * t),
                (int)(from.B + (to.B - from.B) * t));
            using var pen = new Pen(color);
            g.DrawLine(pen, x, 0, x, height - 1);
        }
        return bmp;
    }

    // ---- Analyze: 暗色图像 ----

    [Fact]
    public void Analyze_DarkImage_LooksDark()
    {
        using var bmp = CreateSolidBitmap(100, 100, Color.FromArgb(255, 30, 30, 30));
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.LooksDark.Should().BeTrue();
        stats.AverageLuminance.Should().BeLessThan(112);
    }

    // ---- Analyze: 亮色图像 ----

    [Fact]
    public void Analyze_BrightImage_NotDark()
    {
        using var bmp = CreateSolidBitmap(100, 100, Color.FromArgb(255, 240, 240, 240));
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.LooksDark.Should().BeFalse();
        stats.AverageLuminance.Should().BeGreaterThan(112);
    }

    // ---- Analyze: 低对比度 ----

    [Fact]
    public void Analyze_LowContrastImage()
    {
        // 所有像素亮度接近 → 低对比度
        using var bmp = CreateSolidBitmap(100, 100, Color.FromArgb(255, 128, 128, 128));
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.LowContrast.Should().BeTrue();
    }

    // ---- Analyze: 高对比度（渐变） ----

    [Fact]
    public void Analyze_HighContrastGradient_NotLowContrast()
    {
        using var bmp = CreateGradientBitmap(200, 100, Color.Black, Color.White);
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.LowContrast.Should().BeFalse();
    }

    // ---- Analyze: 纯白图像 ----

    [Fact]
    public void Analyze_WhiteImage()
    {
        using var bmp = CreateSolidBitmap(50, 50, Color.White);
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.AverageLuminance.Should().BeApproximately(255, 1);
        stats.LooksDark.Should().BeFalse();
    }

    // ---- Analyze: 纯黑图像 ----

    [Fact]
    public void Analyze_BlackImage()
    {
        using var bmp = CreateSolidBitmap(50, 50, Color.Black);
        var stats = OcrImagePreprocessor.Analyze(bmp);

        stats.AverageLuminance.Should().BeApproximately(0, 1);
        stats.LooksDark.Should().BeTrue();
    }

    // ---- Prepare: 基本缩放 ----

    [Fact]
    public void Prepare_SmallImage_ScaledUp()
    {
        using var source = CreateSolidBitmap(50, 50, Color.Gray);
        using var result = OcrImagePreprocessor.Prepare(source, 1024);

        // 小图被放大，结果应大于原图
        result.Width.Should().BeGreaterThan(50);
        result.Height.Should().BeGreaterThan(50);
    }

    [Fact]
    public void Prepare_LargeImage_ScaledDown()
    {
        using var source = CreateSolidBitmap(3000, 3000, Color.Gray);
        using var result = OcrImagePreprocessor.Prepare(source, 1024);

        // 大图被缩小，最大边不超过 maxImageDimension + padding
        // padding = min(16, max(0, (1024 - contentSize) / 2))
        // contentSize <= 1024, so result <= 1024 + 32 = 1056
        Math.Max(result.Width, result.Height).Should().BeLessThanOrEqualTo(1056);
    }

    // ---- Prepare: 反色 ----

    [Fact]
    public void Prepare_WithInvert_ProducesResult()
    {
        using var source = CreateSolidBitmap(100, 100, Color.White);
        using var result = OcrImagePreprocessor.Prepare(source, 1024, invert: true);

        result.Should().NotBeNull();
        result.Width.Should().BeGreaterThan(0);
        result.Height.Should().BeGreaterThan(0);
    }

    // ---- Prepare: 二值化 ----

    [Fact]
    public void Prepare_WithBinarize_ProducesResult()
    {
        using var source = CreateSolidBitmap(100, 100, Color.DarkGray);
        using var result = OcrImagePreprocessor.Prepare(source, 1024, binarizeForText: true);

        result.Should().NotBeNull();
        result.Width.Should().BeGreaterThan(0);
    }

    // ---- Prepare: 增强对比度 ----

    [Fact]
    public void Prepare_WithEnhanceContrast_ProducesResult()
    {
        using var source = CreateGradientBitmap(200, 100, Color.FromArgb(255, 80, 80, 80), Color.FromArgb(255, 180, 180, 180));
        using var result = OcrImagePreprocessor.Prepare(source, 1024, enhanceContrast: true);

        result.Should().NotBeNull();
    }

    // ---- Prepare: 太小图像抛异常 ----

    [Fact]
    public void Prepare_TooSmallImage_ThrowsException()
    {
        using var source = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        var act = () => OcrImagePreprocessor.Prepare(source, 1024);
        act.Should().Throw<InvalidOperationException>();
    }

    // ---- Prepare: maxImageDimension 被 clamp ----

    [Fact]
    public void Prepare_MaxDimensionClamped()
    {
        using var source = CreateSolidBitmap(100, 100, Color.Gray);
        // maxImageDimension 太小会被 clamp 到 256
        using var result = OcrImagePreprocessor.Prepare(source, 10);

        result.Should().NotBeNull();
        Math.Max(result.Width, result.Height).Should().BeLessThanOrEqualTo(256 + 32);
    }

    // ---- Prepare: leftCropPixels ----

    [Fact]
    public void Prepare_WithLeftCrop_ProducesResult()
    {
        using var source = CreateSolidBitmap(300, 200, Color.Gray);
        using var result = OcrImagePreprocessor.Prepare(source, 1024, leftCropPixels: 20);

        result.Should().NotBeNull();
        // 裁剪后宽度应小于未裁剪
        result.Width.Should().BeGreaterThan(0);
    }

    // ---- CreateRecoveryCandidates: 暗色图像 ----

    [Fact]
    public void CreateRecoveryCandidates_DarkImage_ReturnsDarkCandidates()
    {
        using var source = CreateSolidBitmap(100, 100, Color.FromArgb(255, 30, 30, 30));
        var stats = OcrImagePreprocessor.Analyze(source);
        stats.LooksDark.Should().BeTrue();

        var candidates = OcrImagePreprocessor.CreateRecoveryCandidates(source, 1024, stats);
        try
        {
            candidates.Should().NotBeEmpty();
            candidates.Should().Contain(c => c.Name.Contains("dark"));
        }
        finally
        {
            foreach (var c in candidates) c.Dispose();
        }
    }

    // ---- CreateRecoveryCandidates: 亮色图像 ----

    [Fact]
    public void CreateRecoveryCandidates_BrightImage_ReturnsContrastCandidate()
    {
        using var source = CreateSolidBitmap(100, 100, Color.FromArgb(255, 200, 200, 200));
        var stats = OcrImagePreprocessor.Analyze(source);
        stats.LooksDark.Should().BeFalse();

        var candidates = OcrImagePreprocessor.CreateRecoveryCandidates(source, 1024, stats);
        try
        {
            candidates.Should().NotBeEmpty();
            candidates.Should().Contain(c => c.Name == "contrast");
        }
        finally
        {
            foreach (var c in candidates) c.Dispose();
        }
    }

    // ---- OcrImageStats: LooksDark 阈值 ----

    [Theory]
    [InlineData(100, 200, true)]   // avg < 112 → dark
    [InlineData(120, 200, false)]  // avg >= 112, high >= 150 → not dark
    [InlineData(120, 140, true)]   // avg >= 112, but high < 150 → dark
    [InlineData(50, 100, true)]    // avg < 112 → dark
    public void OcrImageStats_LooksDark_Threshold(double avg, int high, bool expectedDark)
    {
        var stats = new OcrImageStats(avg, 0, high);
        stats.LooksDark.Should().Be(expectedDark);
    }

    // ---- OcrImageStats: LowContrast 阈值 ----

    [Theory]
    [InlineData(0, 47, true)]    // diff < 48 → low contrast
    [InlineData(0, 48, false)]   // diff >= 48 → not low contrast
    [InlineData(100, 200, false)]
    [InlineData(200, 200, true)]
    public void OcrImageStats_LowContrast_Threshold(int low, int high, bool expectedLowContrast)
    {
        var stats = new OcrImageStats(128, low, high);
        stats.LowContrast.Should().Be(expectedLowContrast);
    }
}
