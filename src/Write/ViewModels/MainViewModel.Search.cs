using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BeexWrite.Services;
using BeexWrite.Views;

namespace BeexWrite.ViewModels;

/// <summary>A single cross-file search hit.</summary>
public sealed class SearchHit
{
    public string FilePath { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Preview { get; init; } = string.Empty;
    public string Display => $"{System.IO.Path.GetFileName(FilePath)}:{Line}";
}

/// <summary>Cross-file search, quick-open and sorting for <see cref="MainViewModel"/>.</summary>
public partial class MainViewModel
{
    [ObservableProperty] private string _searchQuery = string.Empty;
    [ObservableProperty] private bool _searchUseRegex;
    [ObservableProperty] private bool _searchInProgress;
    [ObservableProperty] private string _sortMode = "name";

    public ObservableCollection<SearchHit> SearchResults { get; } = new();

    [RelayCommand]
    private async Task RunFolderSearch()
    {
        SearchResults.Clear();
        var query = SearchQuery.Trim();
        if (query.Length == 0 || FileTree.Count == 0) return;

        var roots = FileTree.Select(r => r.FullPath).ToList();
        var useRegex = SearchUseRegex;
        SearchInProgress = true;
        try
        {
            var hits = await Task.Run(() => Scan(roots, query, useRegex));
            foreach (var h in hits.Take(1000)) SearchResults.Add(h);
        }
        finally
        {
            SearchInProgress = false;
        }
    }

    private static List<SearchHit> Scan(List<string> roots, string query, bool useRegex)
    {
        var hits = new List<SearchHit>();
        Regex? rx = null;
        if (useRegex)
        {
            try { rx = new Regex(query, RegexOptions.IgnoreCase | RegexOptions.Compiled); }
            catch { return hits; }
        }
        foreach (var root in roots)
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(FileService.IsMarkdown);
            }
            catch { continue; }

            foreach (var file in files)
            {
                if (hits.Count >= 1000) return hits;
                string[] lines;
                try { lines = File.ReadAllLines(file); }
                catch { continue; }
                for (var i = 0; i < lines.Length; i++)
                {
                    var match = rx != null
                        ? rx.IsMatch(lines[i])
                        : lines[i].Contains(query, StringComparison.OrdinalIgnoreCase);
                    if (match)
                    {
                        hits.Add(new SearchHit
                        {
                            FilePath = file,
                            Line = i + 1,
                            Preview = lines[i].Trim()
                        });
                        if (hits.Count >= 1000) return hits;
                    }
                }
            }
        }
        return hits;
    }

    [RelayCommand]
    private void OpenSearchHit(SearchHit? hit)
    {
        if (hit is null) return;
        OpenPath(hit.FilePath);
        _bridge.GoToLine(hit.Line);
    }

    [RelayCommand]
    private void QuickOpen()
    {
        if (FileTree.Count == 0)
        {
            OpenFileCommand.Execute(null);
            return;
        }
        var files = EnumerateAllFiles();
        var dlg = new QuickOpenDialog(files) { Owner = HostWindow };
        if (dlg.ShowDialog() == true && dlg.SelectedPath is not null)
        {
            OpenPath(dlg.SelectedPath);
        }
    }

    private List<QuickOpenItem> EnumerateAllFiles()
    {
        var items = new List<QuickOpenItem>();
        foreach (var root in FileTree.Select(r => r.FullPath))
        {
            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                    .Where(FileService.IsMarkdown);
            }
            catch { continue; }
            foreach (var f in files)
            {
                string rel;
                try { rel = System.IO.Path.GetRelativePath(root, f); }
                catch { rel = System.IO.Path.GetFileName(f); }
                items.Add(new QuickOpenItem(f, rel.Replace('\\', '/')));
                if (items.Count >= 5000) return items;
            }
        }
        return items;
    }

    [RelayCommand]
    private void SetSortMode(string? mode)
    {
        SortMode = string.IsNullOrEmpty(mode) ? "name" : mode;
        FileNode.SortMode = mode switch
        {
            "modified" => FileSortMode.Modified,
            "created" => FileSortMode.Created,
            _ => FileSortMode.Name
        };
        RefreshTree();
    }
}
