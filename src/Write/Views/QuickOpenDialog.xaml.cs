using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BeexWrite.Views;

/// <summary>A file entry shown in the quick-open dialog.</summary>
public sealed class QuickOpenItem
{
    public string FullPath { get; }
    public string RelativePath { get; }
    public string Name { get; }

    public QuickOpenItem(string fullPath, string relativePath)
    {
        FullPath = fullPath;
        RelativePath = relativePath;
        Name = Path.GetFileName(fullPath);
    }
}

public partial class QuickOpenDialog : Window
{
    private readonly List<QuickOpenItem> _all;

    public string? SelectedPath { get; private set; }

    public QuickOpenDialog(List<QuickOpenItem> items)
    {
        _all = items;
        Services.ThemeService.Attach(this);
        InitializeComponent();
        Loaded += (_, _) =>
        {
            Filter(string.Empty);
            FilterBox.Focus();
        };
    }

    private void OnFilterChanged(object sender, TextChangedEventArgs e) => Filter(FilterBox.Text);

    private void Filter(string query)
    {
        query = query.Trim();
        IEnumerable<QuickOpenItem> result;
        if (query.Length == 0)
        {
            result = _all.Take(200);
        }
        else
        {
            result = _all
                .Select(item => (item, score: Score(item.RelativePath, query)))
                .Where(x => x.score > 0)
                .OrderByDescending(x => x.score)
                .Take(200)
                .Select(x => x.item);
        }
        ResultsList.ItemsSource = result.ToList();
        if (ResultsList.Items.Count > 0) ResultsList.SelectedIndex = 0;
    }

    // Simple fuzzy score: exact substring beats subsequence; earlier match wins.
    private static int Score(string text, string query)
    {
        var t = text.ToLowerInvariant();
        var q = query.ToLowerInvariant();
        var idx = t.IndexOf(q, StringComparison.Ordinal);
        if (idx >= 0) return 1000 - idx;

        var ti = 0;
        var matched = 0;
        foreach (var c in q)
        {
            var found = t.IndexOf(c, ti);
            if (found < 0) return 0;
            ti = found + 1;
            matched++;
        }
        return matched;
    }

    private void OnFilterKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Move(1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                Accept(ResultsList.SelectedItem as QuickOpenItem);
                e.Handled = true;
                break;
            case Key.Escape:
                DialogResult = false;
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        var count = ResultsList.Items.Count;
        if (count == 0) return;
        var next = ResultsList.SelectedIndex + delta;
        ResultsList.SelectedIndex = Math.Max(0, Math.Min(count - 1, next));
        ResultsList.ScrollIntoView(ResultsList.SelectedItem);
    }

    private void OnResultDoubleClick(object sender, MouseButtonEventArgs e) =>
        Accept(ResultsList.SelectedItem as QuickOpenItem);

    private void Accept(QuickOpenItem? item)
    {
        if (item is null) return;
        SelectedPath = item.FullPath;
        DialogResult = true;
    }
}
