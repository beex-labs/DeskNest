using System;
using System.IO;
using System.IO.Compression;

namespace BeexWrite.Services;

/// <summary>
/// Handles import of .textbundle (directory) and .textpack (zip) formats.
/// TextBundle contains text.md + assets/; TextPack is the zipped version.
/// </summary>
public static class TextBundleService
{
    public static (string Markdown, string? AssetsDir)? Import(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return ImportBundle(path);
            if (path.EndsWith(".textpack", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                return ImportPack(path);
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static (string Markdown, string? AssetsDir)? ImportBundle(string dir)
    {
        var mdPath = Path.Combine(dir, "text.md");
        if (!File.Exists(mdPath))
            mdPath = Path.Combine(dir, "text.markdown");
        if (!File.Exists(mdPath)) return null;

        var md = File.ReadAllText(mdPath);
        var assetsDir = Path.Combine(dir, "assets");
        return (md, Directory.Exists(assetsDir) ? assetsDir : null);
    }

    private static (string Markdown, string? AssetsDir)? ImportPack(string zipPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), $"beex_textpack_{Guid.NewGuid():N}");
        ZipFile.ExtractToDirectory(zipPath, extractDir);

        // Find the .textbundle directory inside
        foreach (var subDir in Directory.GetDirectories(extractDir))
        {
            if (subDir.EndsWith(".textbundle", StringComparison.OrdinalIgnoreCase))
                return ImportBundle(subDir);
        }
        // Fallback: treat extracted root as the bundle
        return ImportBundle(extractDir);
    }
}
