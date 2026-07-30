using System.Collections.Generic;

namespace BeexWrite.Models;

/// <summary>
/// User preferences persisted to %AppData%/BeexWrite/settings.json.
/// New options for later feature phases can be appended without breaking
/// existing files (System.Text.Json ignores unknown members on read).
/// </summary>
public sealed class AppSettings
{
    /// <summary>light, dark, or system (follow OS).</summary>
    public string ThemeMode { get; set; } = "system";

    /// <summary>UI locale: "system", "en", "zh-CN", "zh-TW".</summary>
    public string Locale { get; set; } = "system";

    public bool SidebarVisible { get; set; } = true;
    public bool StatusBarVisible { get; set; } = true;
    public bool SourceMode { get; set; }
    public bool FocusMode { get; set; }
    public bool TypewriterMode { get; set; }

    public double ZoomFactor { get; set; } = 1.0;
    public int EditorWidth { get; set; } = 860;

    public bool AutoSaveEnabled { get; set; } = true;
    public int AutoSaveIntervalSeconds { get; set; } = 5;

    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; }

    // Export settings
    public string ExportPaperSize { get; set; } = "a4";
    public string ExportMargin { get; set; } = "2.5cm";
    public bool ExportBookmarks { get; set; } = true;
    public List<CustomExportItem> CustomExports { get; set; } = new();

    public List<string> RecentFiles { get; set; } = new();
    public List<string> RecentFolders { get; set; } = new();
    public List<string> ClosedFiles { get; set; } = new();

    /// <summary>Last opened document, used for session restore.</summary>
    public string? LastFilePath { get; set; }

    public const int MaxRecent = 12;
    public const int MaxClosed = 10;
}

/// <summary>A user-defined export command (e.g., custom Pandoc invocation).</summary>
public sealed class CustomExportItem
{
    public string Name { get; set; } = "";
    public string Format { get; set; } = "docx";
    public string ExtraArgs { get; set; } = "";
}
