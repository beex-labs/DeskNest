using BeeX.DeskNest;
using FluentAssertions;
using Xunit;

namespace BeeX.DeskNest.Tests.Core;

public class BeeXPathsTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    private string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "BeeXTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    // ---- NormalizeRoot: 路径名为 BeeX 时直接返回 ----

    [Fact]
    public void NormalizeRoot_PathNamedBeeX_ReturnsSamePath()
    {
        var tempDir = CreateTempDir();
        var beeXDir = Path.Combine(tempDir, "BeeX");
        Directory.CreateDirectory(beeXDir);

        var result = BeeXPaths.NormalizeRoot(beeXDir);
        result.Should().Be(Path.GetFullPath(beeXDir));
    }

    [Fact]
    public void NormalizeRoot_PathNamedBeeX_CaseInsensitive()
    {
        var tempDir = CreateTempDir();
        var beeXDir = Path.Combine(tempDir, "beex");
        // 创建名为 "beex" 的目录不是 "BeeX"，所以应该追加 BeeX
        // 但如果路径名恰好是 "beex"（不区分大小写不等于 "BeeX"）
        // 实际上 "beex" != "beex" 不对，"beex" 和 "BeeX" 在 OrdinalIgnoreCase 下是相等的
        Directory.CreateDirectory(beeXDir);

        var result = BeeXPaths.NormalizeRoot(beeXDir);
        // "beex" equals "BeeX" case-insensitively
        result.Should().Be(Path.GetFullPath(beeXDir));
    }

    // ---- NormalizeRoot: 空目录直接返回 ----

    [Fact]
    public void NormalizeRoot_EmptyDirectory_ReturnsSamePath()
    {
        var tempDir = CreateTempDir();
        var emptyDir = Path.Combine(tempDir, "MyData");
        Directory.CreateDirectory(emptyDir);

        var result = BeeXPaths.NormalizeRoot(emptyDir);
        // 目录存在但为空，直接返回
        result.Should().Be(Path.GetFullPath(emptyDir));
    }

    // ---- NormalizeRoot: 非空且非 BeeX 名 → 追加 BeeX 子目录 ----

    [Fact]
    public void NormalizeRoot_NonEmptyNonBeeXDirectory_AppendsBeeX()
    {
        var tempDir = CreateTempDir();
        var nonEmptyDir = Path.Combine(tempDir, "MyStuff");
        Directory.CreateDirectory(nonEmptyDir);
        // 放一个文件使其非空
        File.WriteAllText(Path.Combine(nonEmptyDir, "test.txt"), "hello");

        var result = BeeXPaths.NormalizeRoot(nonEmptyDir);
        result.Should().Be(Path.Combine(Path.GetFullPath(nonEmptyDir), "BeeX"));
    }

    // ---- NormalizeRoot: 路径 trim ----

    [Fact]
    public void NormalizeRoot_TrimsWhitespace()
    {
        var tempDir = CreateTempDir();
        var beeXDir = Path.Combine(tempDir, "BeeX");
        Directory.CreateDirectory(beeXDir);

        var result = BeeXPaths.NormalizeRoot("  " + beeXDir + "  ");
        result.Should().Be(Path.GetFullPath(beeXDir));
    }

    // ---- 子目录路径结构验证 ----

    [Fact]
    public void SubDirectoryPaths_AreUnderRoot()
    {
        // 这些属性都基于 BeeXPaths.Root，验证结构关系
        BeeXPaths.DataDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.ComponentsDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.ScreenshotsDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.RecordingsDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.ClipboardDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.NotesDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.FileBoxesDir.Should().StartWith(BeeXPaths.Root);
        BeeXPaths.CleanerDir.Should().StartWith(BeeXPaths.Root);
    }

    [Fact]
    public void FfmpegDir_IsUnderComponentsDir()
    {
        BeeXPaths.FfmpegDir.Should().Be(Path.Combine(BeeXPaths.ComponentsDir, "ffmpeg"));
    }

    [Fact]
    public void OcrDir_IsUnderComponentsDir()
    {
        BeeXPaths.OcrDir.Should().Be(Path.Combine(BeeXPaths.ComponentsDir, "beex-ocr"));
    }

    [Fact]
    public void StateFile_IsUnderDataDir()
    {
        BeeXPaths.StateFile.Should().Be(Path.Combine(BeeXPaths.DataDir, "state.json"));
    }

    [Fact]
    public void ConfigFile_IsUnderDataDir()
    {
        BeeXPaths.ConfigFile.Should().Be(Path.Combine(BeeXPaths.DataDir, "config.json"));
    }

    // ---- Legacy 路径结构 ----

    [Fact]
    public void LegacyDataDir_IsUnderLocalAppData()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        BeeXPaths.LegacyDataDir.Should().StartWith(localAppData);
    }

    [Fact]
    public void LegacyConfigFile_IsUnderLegacyDataDir()
    {
        BeeXPaths.LegacyConfigFile.Should().Be(Path.Combine(BeeXPaths.LegacyDataDir, "config.json"));
    }
}
