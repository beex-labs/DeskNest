using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BeeXCleaner.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace BeeXCleaner.Views;

/// <summary>
/// 清理历史窗口（6.2 / 9.4 工具箱）：列出 Logs 目录下的会话日志与内部日志，选中查看内容。
/// </summary>
public partial class CleanupHistoryWindow : Window
{
    private sealed record Entry(string Display, string Path)
    {
        public override string ToString() => Display;
    }

    public CleanupHistoryWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadLogs();
    }

    private void LoadLogs()
    {
        LogList.Items.Clear();
        ContentBox.Clear();
        try
        {
            var root = AppPaths.LogsRoot;
            if (!Directory.Exists(root)) return;

            foreach (var file in new DirectoryInfo(root).GetFiles("*.*")
                         .Where(f => f.Extension is ".log")
                         .OrderByDescending(f => f.LastWriteTimeUtc))
            {
                LogList.Items.Add(new Entry($"{file.Name}  ·  {file.LastWriteTime:MM-dd HH:mm}", file.FullName));
            }
        }
        catch (Exception ex) { AppLogger.Warn("加载清理历史失败", ex); }
    }

    private void OnLogChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LogList.SelectedItem is not Entry entry) return;
        try { ContentBox.Text = File.ReadAllText(entry.Path); }
        catch (Exception ex) { ContentBox.Text = $"无法读取日志：{ex.Message}"; }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogsRoot}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开目录", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadLogs();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
