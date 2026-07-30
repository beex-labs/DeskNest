using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using BeexWrite.Services;

namespace BeexWrite.ViewModels;

public enum FileSortMode { Name, Modified, Created }

/// <summary>
/// A node in the folder sidebar. Directories load their children lazily the
/// first time they are expanded to keep large trees responsive.
/// </summary>
public partial class FileNode : ObservableObject
{
    private bool _childrenLoaded;

    public string FullPath { get; }
    public bool IsDirectory { get; }
    public string Name { get; }
    public FileNode? Parent { get; private set; }

    /// <summary>Directory to create children in (self if a folder, else the parent folder).</summary>
    public string DirectoryPath => IsDirectory ? FullPath : (Path.GetDirectoryName(FullPath) ?? FullPath);

    public ObservableCollection<FileNode> Children { get; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public bool ShowNonMarkdown { get; set; }

    public FileNode(string path, bool isDirectory, bool showNonMarkdown)
    {
        FullPath = path;
        IsDirectory = isDirectory;
        ShowNonMarkdown = showNonMarkdown;
        Name = string.IsNullOrEmpty(Path.GetFileName(path)) ? path : Path.GetFileName(path);
        if (isDirectory)
        {
            // Placeholder so the expander arrow shows before children load.
            Children.Add(Placeholder);
        }
    }

    private static readonly FileNode Placeholder = new("", false, false);

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_childrenLoaded)
        {
            LoadChildren();
        }
    }

    public void LoadChildren()
    {
        _childrenLoaded = true;
        Children.Clear();
        if (!IsDirectory) return;

        try
        {
            var dirs = SortDirs(Directory.EnumerateDirectories(FullPath).Where(d => !IsHidden(d)));
            foreach (var d in dirs)
                Children.Add(new FileNode(d, true, ShowNonMarkdown) { Parent = this });

            var files = SortFiles(Directory.EnumerateFiles(FullPath)
                .Where(f => !IsHidden(f) && (ShowNonMarkdown || FileService.IsMarkdown(f))));
            foreach (var f in files)
                Children.Add(new FileNode(f, false, ShowNonMarkdown) { Parent = this });
        }
        catch
        {
            // Access-denied folders simply show no children.
        }
    }

    /// <summary>Active sort order for the folder tree (shared across nodes).</summary>
    public static FileSortMode SortMode { get; set; } = FileSortMode.Name;

    private static IEnumerable<string> SortDirs(IEnumerable<string> paths) => SortMode switch
    {
        FileSortMode.Modified => paths.OrderByDescending(SafeWriteTime),
        FileSortMode.Created => paths.OrderByDescending(SafeCreateTime),
        _ => paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
    };

    private static IEnumerable<string> SortFiles(IEnumerable<string> paths) => SortDirs(paths);

    private static DateTime SafeWriteTime(string p)
    {
        try { return File.GetLastWriteTimeUtc(p); } catch { return DateTime.MinValue; }
    }

    private static DateTime SafeCreateTime(string p)
    {
        try { return File.GetCreationTimeUtc(p); } catch { return DateTime.MinValue; }
    }

    private static bool IsHidden(string path)
    {
        try
        {
            var attr = File.GetAttributes(path);
            return attr.HasFlag(FileAttributes.Hidden) || attr.HasFlag(FileAttributes.System);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Reloads this directory's children from disk.</summary>
    public void Refresh()
    {
        if (!IsDirectory) return;
        var wasExpanded = IsExpanded;
        LoadChildren();
        IsExpanded = wasExpanded;
    }
}
