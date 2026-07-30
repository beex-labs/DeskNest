using System.Reflection;
using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Core;

public class UserConfigHelperTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string? _originalRoot;
    private static readonly FieldInfo CachedRootField = typeof(BeeXPaths).GetField("cachedRoot", BindingFlags.NonPublic | BindingFlags.Static)!;

    public UserConfigHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BeeXConfigTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        // Save original cached root and redirect to temp dir
        _originalRoot = CachedRootField.GetValue(null) as string;
        CachedRootField.SetValue(null, _tempDir);
    }

    public void Dispose()
    {
        // Restore original cached root
        CachedRootField.SetValue(null, _originalRoot);

        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, true); } catch { }
    }

    private string ConfigFilePath => Path.Combine(_tempDir, "Data", "config.json");

    // ---- ReadConfigValue: 文件不存在 ----

    [Fact]
    public void ReadDeepLApiKey_NoConfigFile_ShouldReturnEmpty()
    {
        UserConfigHelper.ReadDeepLApiKey().Should().BeEmpty();
    }

    [Fact]
    public void ReadTranslateTarget_NoConfigFile_ShouldReturnAuto()
    {
        UserConfigHelper.ReadTranslateTarget().Should().Be("auto");
    }

    // ---- WriteConfigValue: 创建新文件 ----

    [Fact]
    public void WriteDeepLApiKey_ShouldCreateConfigFile()
    {
        UserConfigHelper.WriteDeepLApiKey("test-key-123");
        File.Exists(ConfigFilePath).Should().BeTrue();
    }

    [Fact]
    public void WriteDeepLApiKey_ThenRead_ShouldReturnCorrectValue()
    {
        UserConfigHelper.WriteDeepLApiKey("test-key-456");
        UserConfigHelper.ReadDeepLApiKey().Should().Be("test-key-456");
    }

    // ---- Write 保留其他 key ----

    [Fact]
    public void WriteDeepLApiKey_ShouldPreserveOtherKeys()
    {
        UserConfigHelper.WriteTranslateTarget("zh");
        UserConfigHelper.WriteDeepLApiKey("my-key");

        UserConfigHelper.ReadTranslateTarget().Should().Be("zh");
        UserConfigHelper.ReadDeepLApiKey().Should().Be("my-key");
    }

    [Fact]
    public void WriteTranslateTarget_ShouldPreserveOtherKeys()
    {
        UserConfigHelper.WriteDeepLApiKey("my-key");
        UserConfigHelper.WriteTranslateTarget("en");

        UserConfigHelper.ReadDeepLApiKey().Should().Be("my-key");
        UserConfigHelper.ReadTranslateTarget().Should().Be("en");
    }

    // ---- TranslateTarget 读写往返 ----

    [Fact]
    public void TranslateTarget_RoundTrip_ShouldWork()
    {
        foreach (var lang in new[] { "auto", "zh", "en", "ja", "ko" })
        {
            UserConfigHelper.WriteTranslateTarget(lang);
            UserConfigHelper.ReadTranslateTarget().Should().Be(lang);
        }
    }

    [Fact]
    public void ReadTranslateTarget_WithWhitespace_ShouldTrimAndLowercase()
    {
        UserConfigHelper.WriteTranslateTarget("  ZH  ");
        UserConfigHelper.ReadTranslateTarget().Should().Be("zh");
    }

    // ---- DeepL Key 读写往返 ----

    [Fact]
    public void DeepLApiKey_RoundTrip_ShouldWork()
    {
        UserConfigHelper.WriteDeepLApiKey("abc-def-ghi");
        UserConfigHelper.ReadDeepLApiKey().Should().Be("abc-def-ghi");
    }

    [Fact]
    public void DeepLApiKey_Overwrite_ShouldUpdateValue()
    {
        UserConfigHelper.WriteDeepLApiKey("first-key");
        UserConfigHelper.WriteDeepLApiKey("second-key");
        UserConfigHelper.ReadDeepLApiKey().Should().Be("second-key");
    }

    [Fact]
    public void DeepLApiKey_EmptyString_ShouldReturnEmpty()
    {
        UserConfigHelper.WriteDeepLApiKey("");
        UserConfigHelper.ReadDeepLApiKey().Should().BeEmpty();
    }
}
