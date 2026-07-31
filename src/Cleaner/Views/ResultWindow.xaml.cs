using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BeeXCleaner.Views;

/// <summary>
/// Structured Cleanup Results Window (6.2 / 9.4): Displays success/failure, deletion after restart, freed space, backup path, and log,
/// Supports opening logs, opening the backup directory, and exporting to .txt or .json.
/// </summary>
public partial class ResultWindow : Window
{
    private readonly ResidualCleanResult _result;
    private readonly string _title;
    private readonly string? _backupPath;
    private readonly string? _logPath;

    public ResultWindow(ResidualCleanResult result, string title = "清理完成")
    {
        InitializeComponent();
        _result = result;
        _title = title;
        _backupPath = result.BackupPath;
        _logPath = result.LogPath;

        Title = title;
        TitleText.Text = title;
        DeletedText.Text = result.Deleted.ToString();
        RebootText.Text = result.PendingReboot.ToString();
        FailedText.Text = result.Failed.ToString();
        FreedText.Text = InstalledProgram.FormatSize(result.FreedBytes);

        if (!string.IsNullOrWhiteSpace(_backupPath) && Directory.Exists(_backupPath))
            BackupBar.Visibility = Visibility.Visible;

        if (!string.IsNullOrWhiteSpace(_logPath))
        {
            LogPathText.Text = $"日志已保存：{_logPath}";
            LogPathText.Visibility = Visibility.Visible;
        }
        else
        {
            OpenLogBtn.IsEnabled = false;
        }

        LogBox.Text = BuildDetailText();
    }

    /// <summary> Combination Details Text: Use "Clear Logs" first; otherwise, assemble the list using "Delete," "Failure," and "Restart." </summary>
    private string BuildDetailText()
    {
        if (!string.IsNullOrWhiteSpace(_result.Log))
            return _result.Log;

        var sb = new StringBuilder();
        foreach (var d in _result.DeletedItems) sb.AppendLine($"✔ 已删除: {d}");
        foreach (var p in _result.PendingRebootItems) sb.AppendLine($"↻ 重启后删除: {p}");
        foreach (var f in _result.FailedItems) sb.AppendLine($"✗ 失败: {f}");
        foreach (var w in _result.Warnings) sb.AppendLine($"⚠ {w}");
        return sb.ToString().Trim();
    }

    private void OnOpenBackup(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_backupPath) || !Directory.Exists(_backupPath))
        {
            MessageBox.Show(this, "备份目录不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenInExplorer(_backupPath!);
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_logPath) || !File.Exists(_logPath))
        {
            MessageBox.Show(this, "日志文件不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(_logPath!) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开日志", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnExportTxt(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出清理结果",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"BeeXCleaner-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dlg.ShowDialog(this) != true) return;
        TryWrite(dlg.FileName, BuildTextReport());
    }

    private void OnExportJson(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Title = "导出清理结果",
            Filter = "JSON 文件 (*.json)|*.json",
            FileName = $"BeeXCleaner-{DateTime.Now:yyyyMMdd-HHmmss}.json"
        };
        if (dlg.ShowDialog(this) != true) return;

        var payload = new
        {
            title = _title,
            time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            deleted = _result.Deleted,
            failed = _result.Failed,
            pendingReboot = _result.PendingReboot,
            freedBytes = _result.FreedBytes,
            backupPath = _backupPath,
            logPath = _logPath,
            deletedItems = _result.DeletedItems,
            pendingRebootItems = _result.PendingRebootItems,
            failedItems = _result.FailedItems,
            warnings = _result.Warnings
        };
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
        TryWrite(dlg.FileName, json);
    }

    private string BuildTextReport()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"BeeX Cleaner — {_title}");
        sb.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"成功删除: {_result.Deleted} 项");
        sb.AppendLine($"重启后删除: {_result.PendingReboot} 项");
        sb.AppendLine($"失败: {_result.Failed} 项");
        sb.AppendLine($"释放空间: {InstalledProgram.FormatSize(_result.FreedBytes)}");
        if (!string.IsNullOrWhiteSpace(_backupPath)) sb.AppendLine($"注册表备份: {_backupPath}");
        if (!string.IsNullOrWhiteSpace(_logPath)) sb.AppendLine($"日志文件: {_logPath}");
        sb.AppendLine(new string('-', 60));
        sb.AppendLine(BuildDetailText());
        return sb.ToString();
    }

    private void TryWrite(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, Encoding.UTF8);
            MessageBox.Show(this, "已导出。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void OpenInExplorer(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { /* Ignore */ }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}

