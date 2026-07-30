using System;
using System.IO;
using System.Threading.Tasks;
using Markdig;

namespace BeexWrite.Services;

/// <summary>
/// Document export. HTML export is implemented with Markdig; PDF / Word / RTF /
/// ODT / EPUB / LaTeX are planned for a later phase (see docs/FEATURES.md) and
/// currently throw <see cref="NotSupportedException"/> so callers can surface a
/// friendly "coming soon" message.
/// </summary>
public sealed class ExportService
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .UsePipeTables()
        .UseGridTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseFootnotes()
        .UseDefinitionLists()
        .UseGenericAttributes()
        .UseAutoIdentifiers()
        .Build();

    public string RenderHtmlBody(string markdown) => Markdown.ToHtml(markdown ?? string.Empty, Pipeline);

    public async Task ExportHtmlAsync(string markdown, string path, string title, string theme, bool includeStyles)
    {
        var body = RenderHtmlBody(markdown);
        var html = includeStyles
            ? WrapWithStyles(body, title, theme)
            : $"<!doctype html>\n<html>\n<head>\n<meta charset=\"utf-8\">\n<title>{Escape(title)}</title>\n</head>\n<body>\n{body}\n</body>\n</html>\n";
        await File.WriteAllTextAsync(path, html);
    }

    private static string WrapWithStyles(string body, string title, string theme)
    {
        var bg = theme == "dark" ? "#1e1f22" : "#ffffff";
        var fg = theme == "dark" ? "#e6e6e6" : "#1f2328";
        var codeBg = theme == "dark" ? "#2a2d31" : "#f2f3f5";
        var border = theme == "dark" ? "#3a3d41" : "#e1e4e8";
        return $$"""
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>{{Escape(title)}}</title>
<style>
  :root { color-scheme: {{theme}}; }
  body { max-width: 820px; margin: 40px auto; padding: 0 20px;
         font-family: "Segoe UI","Microsoft YaHei UI",system-ui,sans-serif;
         line-height: 1.7; color: {{fg}}; background: {{bg}}; }
  h1,h2,h3,h4,h5,h6 { line-height: 1.3; margin-top: 1.4em; }
  pre,code { font-family: "Cascadia Code",Consolas,monospace; }
  code { background: {{codeBg}}; padding: 2px 5px; border-radius: 4px; }
  pre { background: {{codeBg}}; padding: 14px; border-radius: 8px; overflow: auto; }
  pre code { background: none; padding: 0; }
  blockquote { border-left: 3px solid {{border}}; margin: 0; padding: 4px 16px; color: #6b7280; }
  table { border-collapse: collapse; }
  th,td { border: 1px solid {{border}}; padding: 6px 12px; }
  img { max-width: 100%; }
  hr { border: none; border-top: 2px solid {{border}}; }
</style>
</head>
<body>
{{body}}
</body>
</html>
""";
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
