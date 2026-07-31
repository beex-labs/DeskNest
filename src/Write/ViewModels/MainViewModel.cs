using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using BeexWrite.Models;
using BeexWrite.Services;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BeexWrite.ViewModels;

/// <summary>Primary view model backing <c>MainWindow</c>.</summary>
public partial class MainViewModel : ObservableObject
{
    private const string MarkdownFilter =
        "Markdown (*.md;*.markdown)|*.md;*.markdown;*.mkd;*.mdown;*.mdwn|Text (*.txt)|*.txt|All files (*.*)|*.*";

    private readonly EditorBridge _bridge;
    private readonly FileService _files;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly ExportService _export;
    private readonly ShortcutsService _shortcuts;

    public MainViewModel(EditorBridge bridge, FileService files, SettingsService settings, ThemeService theme, ExportService export, ShortcutsService shortcuts)
    {
        _bridge = bridge;
        _files = files;
        _settings = settings;
        _theme = theme;
        _export = export;
        _shortcuts = shortcuts;

        var s = settings.Settings;
        _sidebarVisible = s.SidebarVisible;
        _statusBarVisible = s.StatusBarVisible;
        _sourceMode = s.SourceMode;
        _focusMode = s.FocusMode;
        _typewriterMode = s.TypewriterMode;
        _zoomFactor = s.ZoomFactor;
        ThemeMode = s.ThemeMode;

        _bridge.Ready += (_, _) => OnEditorReady();
        _bridge.DirtyChanged += (_, dirty) => IsDirty = dirty;
        _bridge.StatsChanged += (_, stats) => ApplyStats(stats);
        _bridge.OutlineChanged += (_, items) => ApplyOutline(items);
        _bridge.SaveRequested += async (_, _) => await SaveAsync();
        _bridge.HostCommandRequested += OnHostCommand;
        _bridge.ImagePasted += OnImagePasted;
        _bridge.OpenUrlRequested += OnOpenUrl;
        _bridge.FileDropped += (_, path) => System.Windows.Application.Current.Dispatcher.Invoke(() => HandleDroppedFile(path));

        RebuildRecent();
    }

    // ---- observable state ---------------------------------------------------

    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private bool _sidebarVisible;
    [ObservableProperty] private bool _statusBarVisible;
    [ObservableProperty] private bool _sourceMode;
    [ObservableProperty] private bool _focusMode;
    [ObservableProperty] private bool _typewriterMode;
    [ObservableProperty] private double _zoomFactor = 1.0;
    [ObservableProperty] private string _themeMode = "system";

    [ObservableProperty] private int _words;
    [ObservableProperty] private int _chars;
    [ObservableProperty] private int _lines;
    [ObservableProperty] private int _readingMinutes;
    [ObservableProperty] private int _selWords;

    public ObservableCollection<OutlineEntry> Outline { get; } = new();
    public ObservableCollection<FileNode> FileTree { get; } = new();
    public ObservableCollection<RecentItem> RecentFiles { get; } = new();
    public ObservableCollection<RecentItem> RecentFolders { get; } = new();

    public string DisplayTitle
    {
        get
        {
            var name = FilePath is null ? "Untitled" : Path.GetFileName(FilePath);
            return $"{(IsDirty ? "\u25CF " : string.Empty)}{name} \u2014 BeexWrite";
        }
    }

    partial void OnFilePathChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnIsDirtyChanged(bool value) => OnPropertyChanged(nameof(DisplayTitle));

    /// <summary>Set by MainWindow to trigger the system print dialog.</summary>
    public Action? PrintHandler { get; set; }

    /// <summary>The editor window hosting this view model (dialog owner).</summary>
    public Window? HostWindow { get; set; }

    /// <summary>Document to load once the editor reports ready (embedded note flow).</summary>
    public string? PendingFile { get; set; }

    /// <summary>Raised after the document was written to disk (manual save or auto-save).</summary>
    public event Action<string>? DocumentSaved;

    internal void RaiseDocumentSaved(string path) => DocumentSaved?.Invoke(path);

    private async void OnHostCommand(object? sender, string command)
    {
        switch (command)
        {
            case "save": await SaveAsync(); break;
            case "saveAs": await SaveAsAsync(); break;
            case "open": await OpenFileAsync(); break;
            case "new": await NewFileAsync(); break;
            case "toggleSidebar": ToggleSidebar(); break;
            case "toggleSource": ToggleSource(); break;
            case "quickOpen": QuickOpen(); break;
            case "zoomIn": ZoomIn(); break;
            case "zoomOut": ZoomOut(); break;
            case "fullScreen": ToggleFullScreen(); break;
            case "print": PrintHandler?.Invoke(); break;
        }
    }

    /// <summary>Handles a file dropped onto the editor area (path from WebView2).</summary>
    public void HandleDroppedFile(string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".txt" or ".mkd" or ".mdown" or ".mdwn")
                OpenPath(path);
        }
        else if (Directory.Exists(path))
        {
            OpenFolderPath(path);
        }
    }

    // ---- editor lifecycle ---------------------------------------------------

    private int _cursorLine = 1;

    private void OnEditorReady()
    {
        _theme.Apply(ThemeMode);
        _bridge.SetTheme(_theme.EffectiveTheme);
        _bridge.SetSourceMode(SourceMode);
        _bridge.SetFocusMode(FocusMode);
        _bridge.SetTypewriterMode(TypewriterMode);
        _bridge.SetZoom(ZoomFactor);
        _bridge.SetShortcuts(_shortcuts.Shortcuts);

        // Embedded note flow: a concrete file was requested — open it and skip
        // draft recovery / last-session restore entirely.
        if (!string.IsNullOrEmpty(PendingFile) && File.Exists(PendingFile))
        {
            LoadFile(PendingFile);
            PendingFile = null;
            _bridge.Focus();
            StartAutoSave();
            LoadCustomCss();
            return;
        }

        // Silent session restore: recover draft if present (unsaved content from last session).
        // Secondary instances skip the draft — it belongs to the primary window.
        if (WriteHost.IsPrimaryInstance && _recovery.TryGetDraft(out var draft, out var meta))
        {
            var draftFileExists = meta.OriginalPath is not null && File.Exists(meta.OriginalPath);
            // If the on-disk file content changed since the draft was written (a real external edit,
            // detected by hash rather than mtime to avoid false positives from antivirus/indexer touches), prefer the newer disk content.
            if (draftFileExists && meta.FileHash is not null)
            {
                var currentHash = Services.RecoveryService.HashFile(meta.OriginalPath);
                if (currentHash is not null && currentHash != meta.FileHash)
                {
                    _recovery.ClearDraft();
                    LoadFile(meta.OriginalPath!);
                    _bridge.Focus();
                    StartAutoSave();
                    LoadCustomCss();
                    return;
                }
            }

            FilePath = draftFileExists ? meta.OriginalPath : null;
            _bridge.SetContent(draft, FilePath);
            IsDirty = FilePath is null; // unsaved new file is dirty
            // Restore cursor position after a brief delay for editor to initialize
            if (meta.CursorLine > 1)
            {
                System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeAsync(
                    () => _bridge.GoToLine(meta.CursorLine),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            _bridge.Focus();
            StartAutoSave();
            LoadCustomCss();
            return;
        }

        // Fallback: reopen the last saved document if it still exists.
        var last = _settings.Settings.LastFilePath;
        if (!string.IsNullOrEmpty(last) && File.Exists(last))
        {
            LoadFile(last);
        }
        else
        {
            _bridge.SetContent(string.Empty);
        }
        _bridge.Focus();
        StartAutoSave();
        LoadCustomCss();
    }

    private void ApplyStats(DocStats stats)
    {
        Words = stats.Words;
        Chars = stats.Chars;
        Lines = stats.Lines;
        ReadingMinutes = stats.ReadingMinutes;
        SelWords = stats.SelWords;
        _cursorLine = stats.CursorLine > 0 ? stats.CursorLine : 1;
    }

    private void ApplyOutline(List<OutlineEntry> items)
    {
        Outline.Clear();
        foreach (var i in items) Outline.Add(i);
    }

    // ---- file commands ------------------------------------------------------

    [RelayCommand]
    private async Task NewFileAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        PushClosed();
        FilePath = null;
        _bridge.SetContent(string.Empty);
        IsDirty = false;
        _bridge.Focus();
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        var dlg = new OpenFileDialog { Filter = MarkdownFilter, Title = "Open" };
        if (dlg.ShowDialog() == true) LoadFile(dlg.FileName);
    }

    [RelayCommand]
    private void OpenFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Open Folder" };
        if (dlg.ShowDialog() == true) OpenFolderPath(dlg.FolderName);
    }

    public void OpenFolderPath(string path)
    {
        if (!Directory.Exists(path)) return;
        FileTree.Clear();
        var root = new FileNode(path, true, showNonMarkdown: false) { IsExpanded = true };
        FileTree.Add(root);
        _settings.AddRecentFolder(path);
        _settings.Save();
        RebuildRecent();
        SidebarVisible = true;
    }

    public async void OpenPath(string path)
    {
        if (!File.Exists(path)) return;
        if (!await ConfirmDiscardAsync()) return;
        LoadFile(path);
    }

    private void LoadFile(string path)
    {
        try
        {
            PushClosed();
            var text = _files.ReadText(path);
            FilePath = path;
            _bridge.SetContent(text, path);
            IsDirty = false;
            _settings.AddRecentFile(path);
            _settings.Settings.LastFilePath = path;
            _settings.Save();
            RebuildRecent();
            _bridge.Focus();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not open file:\n{ex.Message}", "BeexWrite",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (FilePath is null)
        {
            await SaveAsAsync();
            return;
        }
        await WriteCurrentAsync(FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = MarkdownFilter,
            Title = Localization.Strings.Instance.DlgSaveAs,
            FileName = FilePath is null ? "Untitled.md" : Path.GetFileName(FilePath),
            DefaultExt = ".md"
        };
        if (dlg.ShowDialog() != true) return;
        await WriteCurrentAsync(dlg.FileName);
    }

    private async Task WriteCurrentAsync(string path)
    {
        try
        {
            var content = await _bridge.RequestContentAsync();
            _files.WriteText(path, content);
            FilePath = path;
            IsDirty = false;
            _bridge.MarkSaved();
            _settings.AddRecentFile(path);
            _settings.Settings.LastFilePath = path;
            _settings.Save();
            RebuildRecent();
            // File is saved — clear the temp recovery draft.
            _recovery.ClearDraft();
            DocumentSaved?.Invoke(path);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save file:\n{ex.Message}", "BeexWrite",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html",
            Title = Localization.Strings.Instance.DlgExportHtml,
            FileName = (FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath)) + ".html",
            DefaultExt = ".html"
        };
        if (dlg.ShowDialog() != true) return;
        var content = await _bridge.RequestContentAsync();
        var title = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        await _export.ExportHtmlAsync(content, dlg.FileName, title, _theme.EffectiveTheme, includeStyles: true);
    }

    [RelayCommand]
    private async Task ExportHtmlPlainAsync()
    {
        var dlg = new SaveFileDialog
        {
            Filter = "HTML (*.html)|*.html",
            Title = Localization.Strings.Instance.DlgExportHtmlPlain,
            FileName = (FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath)) + ".html",
            DefaultExt = ".html"
        };
        if (dlg.ShowDialog() != true) return;
        var content = await _bridge.RequestContentAsync();
        var title = FilePath is null ? "Untitled" : Path.GetFileNameWithoutExtension(FilePath);
        await _export.ExportHtmlAsync(content, dlg.FileName, title, _theme.EffectiveTheme, includeStyles: false);
    }

    [RelayCommand]
    private void OpenRecent(string? path)
    {
        if (!string.IsNullOrEmpty(path)) OpenPath(path);
    }

    [RelayCommand]
    private void Exit() => HostWindow?.Close();

    /// <summary>Tracks the current file as "closed" before switching away.</summary>
    private void PushClosed()
    {
        if (FilePath is null || !File.Exists(FilePath)) return;
        var list = _settings.Settings.ClosedFiles;
        list.RemoveAll(p => string.Equals(p, FilePath, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, FilePath);
        while (list.Count > AppSettings.MaxClosed) list.RemoveAt(list.Count - 1);
        _settings.Save();
    }

    [RelayCommand]
    private void About()
    {
        var about = new Views.AboutWindow { Owner = HostWindow };
        about.ShowDialog();
    }

    [RelayCommand]
    private void NewWindow()
    {
        // Embedded mode: spawning another DeskNest process makes no sense; ignored.
    }

    [RelayCommand]
    private void ReopenClosed()
    {
        var list = _settings.Settings.ClosedFiles;
        while (list.Count > 0)
        {
            var path = list[0];
            list.RemoveAt(0);
            _settings.Save();
            if (File.Exists(path))
            {
                OpenPath(path);
                return;
            }
        }
    }

    [RelayCommand]
    private void ShowShortcuts()
    {
        var dlg = new Views.ShortcutsWindow(_shortcuts) { Owner = HostWindow };
        dlg.ShowDialog();
    }

    [RelayCommand]
    private void ShowFeatureTracker()
    {
        // Try to open docs/FEATURES.md from the opened folder tree
        if (FileTree.Count > 0)
        {
            var rootPath = FileTree[0].FullPath;
            var featPath = Path.Combine(rootPath, "docs", "FEATURES.md");
            if (File.Exists(featPath)) { OpenPath(featPath); return; }
        }
        // Fallback: try exe directory
        var exeDir = AppContext.BaseDirectory;
        var fallback = Path.Combine(exeDir, "FEATURES.md");
        if (File.Exists(fallback)) { OpenPath(fallback); return; }
        MessageBox.Show(
            Localization.Strings.Instance.MsgFeatureTracker,
            "BeexWrite", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- link open handler --------------------------------------------------

    private void OnOpenUrl(object? sender, string url)
    {
        try
        {
            if (url.StartsWith('#'))
            {
                // Internal anchor: find heading by slug and jump
                var slug = url[1..];
                foreach (var entry in Outline)
                {
                    if (string.Equals(entry.Slug, slug, StringComparison.OrdinalIgnoreCase))
                    {
                        _bridge.GoToLine(entry.Line);
                        return;
                    }
                }
                return;
            }
            if (url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return;
            }
            if (url.StartsWith("file:///", StringComparison.OrdinalIgnoreCase))
            {
                var localPath = new Uri(url).LocalPath;
                OpenLocalPath(localPath);
                return;
            }
            // Relative path: resolve against current document
            if (FilePath is not null)
            {
                var dir = Path.GetDirectoryName(FilePath)!;
                var resolved = Path.GetFullPath(Path.Combine(dir, url));
                OpenLocalPath(resolved);
            }
        }
        catch { }
    }

    private void OpenLocalPath(string path)
    {
        if (File.Exists(path))
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".md" or ".markdown" or ".txt" or ".mkd" or ".mdown")
                OpenPath(path);
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        else if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
    }

    [RelayCommand]
    private void OpenThemeFolder()
    {
        var dir = System.IO.Path.Combine(_settings.SettingsDirectory, "themes");
        System.IO.Directory.CreateDirectory(dir);
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
    }

    [RelayCommand]
    private void OpenPreferences()
    {
        var dlg = new Views.PreferencesWindow(_settings.Settings) { Owner = HostWindow };
        if (dlg.ShowDialog() == true)
        {
            _settings.Save();
            ThemeMode = _settings.Settings.ThemeMode;
            SidebarVisible = _settings.Settings.SidebarVisible;
            StatusBarVisible = _settings.Settings.StatusBarVisible;
            SourceMode = _settings.Settings.SourceMode;
            FocusMode = _settings.Settings.FocusMode;
            TypewriterMode = _settings.Settings.TypewriterMode;
            if (_bridge.IsAttached) _bridge.SetEditorWidth(_settings.Settings.EditorWidth);
        }
    }

    /// <summary>Loads all CSS files from the themes folder (user.css last) and injects into the editor.
    /// Files starting with "_" or ending in ".disabled.css" are skipped so users can stage themes.</summary>
    public void LoadCustomCss()
    {
        var dir = System.IO.Path.Combine(_settings.SettingsDirectory, "themes");
        if (!System.IO.Directory.Exists(dir)) return;
        try
        {
            var sb = new System.Text.StringBuilder();
            // Third-party themes first, user.css last so user overrides win.
            foreach (var f in System.IO.Directory.GetFiles(dir, "*.css")
                         .Where(f =>
                         {
                             var name = System.IO.Path.GetFileName(f);
                             return !name.StartsWith('_') && !name.EndsWith(".disabled.css", StringComparison.OrdinalIgnoreCase);
                         })
                         .OrderBy(f =>
                         string.Equals(System.IO.Path.GetFileName(f), "user.css", StringComparison.OrdinalIgnoreCase) ? 1 : 0))
            {
                sb.AppendLine($"/* {System.IO.Path.GetFileName(f)} */");
                sb.AppendLine(System.IO.File.ReadAllText(f));
            }
            if (sb.Length > 0) _bridge.SetCustomCss(sb.ToString());
        }
        catch { }
    }

    // ---- editor / formatting commands --------------------------------------

    [RelayCommand]
    private void ExecEditor(string? command)
    {
        if (!string.IsNullOrEmpty(command)) _bridge.Exec(command);
    }

    [RelayCommand]
    private void GoToOutline(OutlineEntry? entry)
    {
        if (entry != null) _bridge.GoToLine(entry.Line);
    }

    [RelayCommand]
    private void InsertTable()
    {
        var dlg = new Views.InsertTableDialog { Owner = HostWindow };
        if (dlg.ShowDialog() == true)
        {
            _bridge.Exec("table", new { rows = dlg.Rows, cols = dlg.Columns });
        }
    }

    // ---- view commands ------------------------------------------------------

    [RelayCommand]
    private void ToggleSidebar() => SidebarVisible = !SidebarVisible;

    [RelayCommand]
    private void ToggleStatusBar() => StatusBarVisible = !StatusBarVisible;

    [RelayCommand]
    private void ToggleSource() => SourceMode = !SourceMode;

    [RelayCommand]
    private void ToggleFocus() => FocusMode = !FocusMode;

    [RelayCommand]
    private void ToggleTypewriter() => TypewriterMode = !TypewriterMode;

    [RelayCommand]
    private void ZoomIn() => SetZoom(ZoomFactor + 0.1);

    [RelayCommand]
    private void ZoomOut() => SetZoom(ZoomFactor - 0.1);

    [RelayCommand]
    private void ZoomReset() => SetZoom(1.0);

    [ObservableProperty] private bool _isFullScreen;
    [ObservableProperty] private bool _alwaysOnTop;

    [RelayCommand]
    private void ToggleFullScreen() => IsFullScreen = !IsFullScreen;

    [RelayCommand]
    private void ToggleAlwaysOnTop() => AlwaysOnTop = !AlwaysOnTop;

    [RelayCommand]
    private void AdjustEditorWidth(string? w)
    {
        if (int.TryParse(w, out var v) && v >= 400 && v <= 2000)
        {
            _settings.Settings.EditorWidth = v;
            _settings.Save();
            if (_bridge.IsAttached) _bridge.SetEditorWidth(v);
        }
    }

    private void SetZoom(double factor)
    {
        ZoomFactor = Math.Clamp(Math.Round(factor, 2), 0.5, 3.0);
    }

    [RelayCommand]
    private void SetTheme(string? mode)
    {
        if (string.IsNullOrEmpty(mode)) return;
        ThemeMode = mode;
    }

    // ---- reactions that push to editor / persist ----------------------------

    partial void OnSidebarVisibleChanged(bool value) { _settings.Settings.SidebarVisible = value; _settings.Save(); }
    partial void OnStatusBarVisibleChanged(bool value) { _settings.Settings.StatusBarVisible = value; _settings.Save(); }

    partial void OnSourceModeChanged(bool value)
    {
        if (_bridge.IsAttached) _bridge.SetSourceMode(value);
        _settings.Settings.SourceMode = value;
        _settings.Save();
    }

    partial void OnFocusModeChanged(bool value)
    {
        if (_bridge.IsAttached) _bridge.SetFocusMode(value);
        _settings.Settings.FocusMode = value;
        _settings.Save();
    }

    partial void OnTypewriterModeChanged(bool value)
    {
        if (_bridge.IsAttached) _bridge.SetTypewriterMode(value);
        _settings.Settings.TypewriterMode = value;
        _settings.Save();
    }

    partial void OnZoomFactorChanged(double value)
    {
        if (_bridge.IsAttached) _bridge.SetZoom(value);
        _settings.Settings.ZoomFactor = value;
        _settings.Save();
    }

    partial void OnThemeModeChanged(string value)
    {
        _theme.Apply(value);
        if (_bridge.IsAttached) _bridge.SetTheme(_theme.EffectiveTheme);
        _settings.Settings.ThemeMode = value;
        _settings.Save();
    }

    // ---- helpers ------------------------------------------------------------

    private void RebuildRecent()
    {
        RecentFiles.Clear();
        foreach (var p in _settings.Settings.RecentFiles)
            RecentFiles.Add(new RecentItem(p));
        RecentFolders.Clear();
        foreach (var p in _settings.Settings.RecentFolders)
            RecentFolders.Add(new RecentItem(p));
    }

    [RelayCommand]
    private void OpenRecentFolder(string? path)
    {
        if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) OpenFolderPath(path);
    }

    /// <summary>Returns false if the user cancels an operation that would drop unsaved changes.</summary>
    private async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsDirty) return true;
        var result = MessageBox.Show(
            Localization.Strings.Instance.UnsavedPrompt,
            "BeexWrite", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        switch (result)
        {
            case MessageBoxResult.Yes:
                await SaveAsync();
                return !IsDirty;
            case MessageBoxResult.No:
                return true;
            default:
                return false;
        }
    }

    public bool HasUnsavedChanges => IsDirty;

    public async Task<string> GetContentAsync() => await _bridge.RequestContentAsync();

    public void PersistWindow(double width, double height, bool maximized)
    {
        _settings.Settings.WindowWidth = width;
        _settings.Settings.WindowHeight = height;
        _settings.Settings.WindowMaximized = maximized;
        _settings.Save();
    }
}

/// <summary>Recent-file entry with a friendly display name.</summary>
public sealed class RecentItem
{
    public string FullPath { get; }
    public string Name { get; }

    public RecentItem(string path)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
    }
}
