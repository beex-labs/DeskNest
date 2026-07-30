using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using BeexWrite.Services;
using BeexWrite.Views;
using Clipboard = System.Windows.Clipboard;
using MessageBox = System.Windows.MessageBox;

namespace BeexWrite.ViewModels;

/// <summary>File-tree operations and auto-save for <see cref="MainViewModel"/>.</summary>
public partial class MainViewModel
{
    private DispatcherTimer? _autoSaveTimer;
    private readonly RecoveryService _recovery = new();

    public void StartAutoSave()
    {
        _autoSaveTimer?.Stop();
        var seconds = Math.Max(2, _settings.Settings.AutoSaveIntervalSeconds);
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _autoSaveTimer.Tick += async (_, _) =>
        {
            if (!IsDirty) return;
            string content;
            try { content = await _bridge.RequestContentAsync(); }
            catch { return; }

            // Always keep a crash-recovery draft while dirty (primary instance only,
            // to avoid multi-window clobbering).
            if (WriteHost.IsPrimaryInstance)
                _recovery.SaveDraft(FilePath, content, _cursorLine);

            // Auto-save named files in place.
            if (_settings.Settings.AutoSaveEnabled && FilePath is not null)
            {
                try
                {
                    _files.WriteText(FilePath, content);
                    IsDirty = false;
                    _bridge.MarkSaved();
                    _settings.Settings.LastFilePath = FilePath;
                    RaiseDocumentSaved(FilePath);
                }
                catch { /* ignore transient IO errors */ }
            }
        };
        _autoSaveTimer.Start();
    }

    private bool _discardDraftOnExit;

    /// <summary>User explicitly chose "Don't Save" — clear the recovery draft and
    /// suppress the exit-time draft snapshot so discarded changes don't resurrect.</summary>
    public void DiscardRecoveryDraft()
    {
        _discardDraftOnExit = true;
        _recovery.ClearDraft();
    }

    /// <summary>Called on a clean shutdown — persist current state for next launch.</summary>
    public async void OnCleanExit()
    {
        if (!WriteHost.IsPrimaryInstance) return; // secondary windows don't own the draft
        if (_discardDraftOnExit) return;    // user chose "Don't Save"
        try
        {
            var content = await _bridge.RequestContentAsync();
            _recovery.SaveDraft(FilePath, content, _cursorLine);
        }
        catch
        {
            // Best-effort; if bridge is gone, just keep whatever draft exists.
        }
    }

    /// <summary>Handles an image pasted/dropped into the editor: saves to assets folder and inserts.</summary>
    private async void OnImagePasted(object? sender, (string Data, string Name) e)
    {
        var dir = FilePath is not null ? Path.GetDirectoryName(FilePath) : _settings.SettingsDirectory;
        if (string.IsNullOrEmpty(dir)) return;

        // Per-document override: YAML front matter `assets: <dir>` (Typora-style copy-images-to).
        var assetsFolder = "assets";
        try
        {
            var content = await _bridge.RequestContentAsync();
            var fmMatch = System.Text.RegularExpressions.Regex.Match(
                content, @"^---\s*\n(.*?)\n---", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (fmMatch.Success)
            {
                var m = System.Text.RegularExpressions.Regex.Match(
                    fmMatch.Groups[1].Value, @"^(?:assets|typora-copy-images-to):\s*(.+)$",
                    System.Text.RegularExpressions.RegexOptions.Multiline);
                if (m.Success)
                {
                    var custom = m.Groups[1].Value.Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(custom) && !Path.IsPathRooted(custom) && !custom.Contains(".."))
                        assetsFolder = custom;
                }
            }
        }
        catch { /* fall back to default assets folder */ }

        var assetsDir = Path.Combine(dir, assetsFolder);
        try
        {
            Directory.CreateDirectory(assetsDir);
            // Defence-in-depth: strip any directory components from the supplied name.
            var safeName = Path.GetFileName(e.Name);
            if (string.IsNullOrWhiteSpace(safeName)) safeName = $"image-{DateTime.Now.Ticks}.png";
            var path = Path.Combine(assetsDir, safeName);
            var base64 = e.Data;
            var commaIdx = base64.IndexOf(',');
            if (commaIdx >= 0) base64 = base64[(commaIdx + 1)..];
            File.WriteAllBytes(path, Convert.FromBase64String(base64));
            var relPath = assetsFolder.Replace('\\', '/') + "/" + safeName;
            _bridge.Exec("image", new { alt = "", url = relPath });
        }
        catch { /* silently ignore save failures */ }
    }

    [RelayCommand]
    private void OpenNode(FileNode? node)
    {
        if (node is { IsDirectory: false }) OpenPath(node.FullPath);
    }

    [RelayCommand]
    private void RefreshTree()
    {
        foreach (var root in FileTree) root.Refresh();
    }

    [RelayCommand]
    private void RevealInExplorer(FileNode? node)
    {
        if (node is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{node.FullPath}\"") { UseShellExecute = true });
        }
        catch { /* ignore */ }
    }

    [RelayCommand]
    private void CopyNodePath(FileNode? node)
    {
        if (node is null) return;
        try { Clipboard.SetText(node.FullPath); } catch { /* ignore */ }
    }

    [RelayCommand]
    private void InsertLinkToNode(FileNode? node)
    {
        if (node is null || node.IsDirectory) return;
        var url = node.FullPath;
        if (FilePath is not null)
        {
            var baseDir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(baseDir))
            {
                try { url = Path.GetRelativePath(baseDir, node.FullPath); } catch { /* keep absolute */ }
            }
        }
        url = url.Replace('\\', '/');
        _bridge.Exec("link", new { text = Path.GetFileNameWithoutExtension(node.FullPath), url });
    }

    [RelayCommand]
    private void RenameNode(FileNode? node)
    {
        if (node is null) return;
        var dlg = new PromptDialog(Localization.Strings.Instance.DlgRename, Localization.Strings.Instance.DlgRenamePrompt, node.Name) { Owner = HostWindow };
        if (dlg.ShowDialog() != true) return;

        var dir = Path.GetDirectoryName(node.FullPath);
        if (string.IsNullOrEmpty(dir)) return;
        var target = Path.Combine(dir, dlg.Value);
        try
        {
            if (node.IsDirectory) Directory.Move(node.FullPath, target);
            else File.Move(node.FullPath, target);

            if (!node.IsDirectory && string.Equals(FilePath, node.FullPath, StringComparison.OrdinalIgnoreCase))
                FilePath = target;

            node.Parent?.Refresh();
            if (node.Parent is null) RefreshRootAfterStructureChange();
        }
        catch (Exception ex)
        {
            Warn($"Could not rename:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void DuplicateNode(FileNode? node)
    {
        if (node is null || node.IsDirectory) return;
        var dir = Path.GetDirectoryName(node.FullPath);
        if (string.IsNullOrEmpty(dir)) return;
        var stem = Path.GetFileNameWithoutExtension(node.FullPath);
        var ext = Path.GetExtension(node.FullPath);
        try
        {
            var target = Path.Combine(dir, $"{stem} copy{ext}");
            var i = 2;
            while (File.Exists(target)) target = Path.Combine(dir, $"{stem} copy {i++}{ext}");
            File.Copy(node.FullPath, target);
            node.Parent?.Refresh();
        }
        catch (Exception ex)
        {
            Warn($"Could not duplicate:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void DeleteNode(FileNode? node)
    {
        if (node is null) return;
        var result = MessageBox.Show(
            string.Format(Localization.Strings.Instance.MsgDeleteConfirm, node.Name),
            "BeexWrite", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK) return;
        try
        {
            if (node.IsDirectory) Directory.Delete(node.FullPath, recursive: true);
            else File.Delete(node.FullPath);
            node.Parent?.Refresh();
        }
        catch (Exception ex)
        {
            Warn($"Could not delete:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void NewFileInNode(FileNode? node)
    {
        var dir = node?.DirectoryPath ?? (FileTree.Count > 0 ? FileTree[0].FullPath : null);
        if (dir is null) return;
        var dlg = new PromptDialog(Localization.Strings.Instance.DlgNewFile, Localization.Strings.Instance.DlgNewFilePrompt, "untitled.md") { Owner = HostWindow };
        if (dlg.ShowDialog() != true) return;
        var name = dlg.Value.Contains('.') ? dlg.Value : dlg.Value + ".md";
        var target = Path.Combine(dir, name);
        try
        {
            if (!File.Exists(target)) File.WriteAllText(target, string.Empty);
            RefreshContaining(node);
            OpenPath(target);
        }
        catch (Exception ex)
        {
            Warn($"Could not create file:\n{ex.Message}");
        }
    }

    [RelayCommand]
    private void NewFolderInNode(FileNode? node)
    {
        var dir = node?.DirectoryPath ?? (FileTree.Count > 0 ? FileTree[0].FullPath : null);
        if (dir is null) return;
        var dlg = new PromptDialog(Localization.Strings.Instance.DlgNewFolder, Localization.Strings.Instance.DlgNewFolderPrompt, "New Folder") { Owner = HostWindow };
        if (dlg.ShowDialog() != true) return;
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, dlg.Value));
            RefreshContaining(node);
        }
        catch (Exception ex)
        {
            Warn($"Could not create folder:\n{ex.Message}");
        }
    }

    private void RefreshContaining(FileNode? node)
    {
        if (node is null) { RefreshTree(); return; }
        if (node.IsDirectory) node.Refresh();
        else node.Parent?.Refresh();
    }

    private void RefreshRootAfterStructureChange() => RefreshTree();

    private static void Warn(string message) =>
        MessageBox.Show(message, "BeexWrite", MessageBoxButton.OK, MessageBoxImage.Warning);
}
