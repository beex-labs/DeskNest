using System.Collections.Generic;

namespace BeexWrite.Models;

/// <summary>Document statistics reported by the web editor.</summary>
public sealed class DocStats
{
    public int Words { get; set; }
    public int Chars { get; set; }
    public int Lines { get; set; }
    public int ReadingMinutes { get; set; }
    public int SelWords { get; set; }
    public int CursorLine { get; set; }
}

/// <summary>A heading entry for the outline sidebar.</summary>
public sealed class OutlineEntry
{
    public int Level { get; set; }
    public string Text { get; set; } = string.Empty;
    public int Line { get; set; }
    public string Slug { get; set; } = string.Empty;
}

/// <summary>Inline-format state at the caret, used to check menu items.</summary>
public sealed class CursorContext
{
    public bool Bold { get; set; }
    public bool Italic { get; set; }
    public bool Strikethrough { get; set; }
    public bool InlineCode { get; set; }
    public int Heading { get; set; }
    public bool SourceMode { get; set; }
}

/// <summary>Aggregated payload for outline messages.</summary>
public sealed class OutlinePayload
{
    public List<OutlineEntry> Items { get; set; } = new();
}
