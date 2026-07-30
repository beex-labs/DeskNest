using System;
using System.IO;
using System.Text;

namespace BeexWrite.Services;

/// <summary>Native file IO for Markdown documents.</summary>
public sealed class FileService
{
    public static readonly string[] MarkdownExtensions =
    {
        ".md", ".markdown", ".mdown", ".mkd", ".mkdn", ".mdwn", ".mdtxt", ".text", ".txt"
    };

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string ReadText(string path) => File.ReadAllText(path);

    public void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, Utf8NoBom);
    }

    public static bool IsMarkdown(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var e in MarkdownExtensions)
        {
            if (string.Equals(ext, e, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
