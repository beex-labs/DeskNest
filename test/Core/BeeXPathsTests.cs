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

    // ---- NormalizeRoot: Returns directly when the path name is "BeeX" ----

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
        // Create a directory named "beex"—not "BeeX"—so "BeeX" should be appended.
        // However, if the path name happens to be "beex" (case-insensitive and not equal to "BeeX")
        // Actually, "beex" != "beex" is incorrect; "beex" and "BeeX" are considered equal under OrdinalIgnoreCase.
        Directory.CreateDirectory(beeXDir);

        var result = BeeXPaths.NormalizeRoot(beeXDir);
        // "beex" equals "BeeX" case-insensitively
        result.Should().Be(Path.GetFullPath(beeXDir));
    }

    // ---- NormalizeRoot: Returns immediately if the directory is empty ----

    [Fact]
    public void NormalizeRoot_EmptyDirectory_ReturnsSamePath()
    {
        var tempDir = CreateTempDir();
        var emptyDir = Path.Combine(tempDir, "MyData");
        Directory.CreateDirectory(emptyDir);

        var result = BeeXPaths.NormalizeRoot(emptyDir);
        // The directory exists but is empty; return immediately.
        result.Should().Be(Path.GetFullPath(emptyDir));
    }

    // ---- NormalizeRoot: Non-empty and not a BeeX name → Add BeeX subdirectory ----

    [Fact]
    public void NormalizeRoot_NonEmptyNonBeeXDirectory_AppendsBeeX()
    {
        var tempDir = CreateTempDir();
        var nonEmptyDir = Path.Combine(tempDir, "MyStuff");
        Directory.CreateDirectory(nonEmptyDir);
        // Place a file there so that it is not empty
        File.WriteAllText(Path.Combine(nonEmptyDir, "test.txt"), "hello");

        var result = BeeXPaths.NormalizeRoot(nonEmptyDir);
        result.Should().Be(Path.Combine(Path.GetFullPath(nonEmptyDir), "BeeX"));
    }

    // ---- NormalizeRoot: Path trim ----

    [Fact]
    public void NormalizeRoot_TrimsWhitespace()
    {
        var tempDir = CreateTempDir();
        var beeXDir = Path.Combine(tempDir, "BeeX");
        Directory.CreateDirectory(beeXDir);

        var result = BeeXPaths.NormalizeRoot("  " + beeXDir + "  ");
        result.Should().Be(Path.GetFullPath(beeXDir));
    }

    // ---- Subdirectory Path Structure Validation ----

    [Fact]
    public void SubDirectoryPaths_AreUnderRoot()
    {
        // These properties are all based on BeeXPaths.Root and validate structural relationships.
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

    // ---- Legacy Path Structure ----

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
