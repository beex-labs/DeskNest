using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace BeexWrite.Services;

/// <summary>
/// Wrapper around the Pandoc universal document converter. If Pandoc is not
/// installed, all methods gracefully fail with a user-friendly message.
/// </summary>
public sealed class PandocService
{
    private string? _pandocPath;

    public bool IsAvailable => FindPandoc() is not null;

    public string? FindPandoc()
    {
        if (_pandocPath is not null && File.Exists(_pandocPath)) return _pandocPath;
        // Check PATH
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in paths)
        {
            var candidate = Path.Combine(dir, "pandoc.exe");
            if (File.Exists(candidate)) { _pandocPath = candidate; return candidate; }
        }
        // Common install locations
        foreach (var loc in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Pandoc", "pandoc.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pandoc", "pandoc.exe")
        })
        {
            if (File.Exists(loc)) { _pandocPath = loc; return loc; }
        }
        return null;
    }

    /// <summary>Converts a file from one format to Markdown.</summary>
    public async Task<string?> ImportToMarkdownAsync(string inputPath)
    {
        var pandoc = FindPandoc();
        if (pandoc is null) return null;
        var ext = Path.GetExtension(inputPath).TrimStart('.').ToLowerInvariant();
        var fromFmt = ext switch
        {
            "docx" => "docx",
            "rtf" => "rtf",
            "odt" => "odt",
            "html" or "htm" => "html",
            "epub" => "epub",
            "tex" or "latex" => "latex",
            _ => ext
        };
        return await RunAsync($"-f {fromFmt} -t markdown --wrap=none \"{inputPath}\"");
    }

    /// <summary>Exports Markdown content to the specified output file.</summary>
    public async Task<bool> ExportAsync(string markdownContent, string outputPath, string? toFormat = null, string? extraArgs = null)
    {
        var pandoc = FindPandoc();
        if (pandoc is null) return false;
        var ext = Path.GetExtension(outputPath).TrimStart('.').ToLowerInvariant();
        var fmt = toFormat ?? ext switch
        {
            "docx" => "docx",
            "rtf" => "rtf",
            "odt" => "odt",
            "epub" => "epub",
            "tex" or "latex" => "latex",
            "html" or "htm" => "html",
            "pdf" => "pdf",
            _ => ext
        };
        var tmpMd = Path.Combine(Path.GetTempPath(), $"beex_{Guid.NewGuid():N}.md");
        try
        {
            await File.WriteAllTextAsync(tmpMd, markdownContent);
            var args = $"-f markdown -t {fmt} -o \"{outputPath}\" \"{tmpMd}\"";
            if (!string.IsNullOrEmpty(extraArgs)) args += " " + extraArgs;
            var result = await RunAsync(args);
            return result is not null;
        }
        finally
        {
            try { File.Delete(tmpMd); } catch { }
        }
    }

    private async Task<string?> RunAsync(string args)
    {
        var pandoc = FindPandoc();
        if (pandoc is null) return null;
        try
        {
            var psi = new ProcessStartInfo(pandoc, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;
            var output = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return proc.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }
}
