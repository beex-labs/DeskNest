using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using BeeXCleaner.Infrastructure;
using MessageBox = System.Windows.MessageBox;

namespace BeeXCleaner.Views;

/// <summary>
/// Backup and Restore Window (9.4 Toolbox): Lists the .reg registry backups for each cleanup session; you can open the directory or run the .reg file to restore.
/// </summary>
public partial class BackupRestoreWindow : Window
{
    /// <summary>List item: Display name + full path.</summary>
    private sealed record Entry(string Display, string Path)
    {
        public override string ToString() => Display;
    }

    public BackupRestoreWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSessions();
    }

    private void LoadSessions()
    {
        SessionList.Items.Clear();
        FileList.Items.Clear();
        try
        {
            var root = AppPaths.BackupsRoot;
            if (!Directory.Exists(root)) return;

            foreach (var dir in new DirectoryInfo(root).GetDirectories()
                         .OrderByDescending(d => d.CreationTimeUtc))
            {
                var count = SafeCount(dir.FullName);
                SessionList.Items.Add(new Entry($"{dir.Name}  ·  {count} 个备份", dir.FullName));
            }
        }
        catch (Exception ex) { AppLogger.Warn("加载备份会话失败", ex); }
    }

    private static int SafeCount(string dir)
    {
        try { return Directory.GetFiles(dir).Length; }
        catch { return 0; }
    }

    private void OnSessionChanged(object sender, SelectionChangedEventArgs e)
    {
        FileList.Items.Clear();
        if (SessionList.SelectedItem is not Entry session) return;
        try
        {
            foreach (var file in Directory.GetFiles(session.Path).OrderBy(f => f))
                FileList.Items.Add(new Entry(Path.GetFileName(file), file));
        }
        catch (Exception ex) { AppLogger.Warn("加载备份文件失败", ex); }
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (SessionList.SelectedItem is not Entry session)
        {
            MessageBox.Show(this, "请先选择一个清理会话。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{session.Path}\"") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "打开目录", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void OnRestore(object sender, RoutedEventArgs e)
    {
        if (FileList.SelectedItem is not Entry file)
        {
            MessageBox.Show(this, "请先在右侧选择一个 .reg 备份文件。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!file.Path.EndsWith(".reg", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "仅 .reg 文件可自动恢复。PATH 备份为文本，请手动查看后恢复。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(this,
            $"将把以下备份写回注册表：\n\n{file.Path}\n\n这会覆盖当前对应注册表项。确定恢复吗？",
            "恢复注册表", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"import \"{file.Path}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                MessageBox.Show(this, "无法启动 reg.exe。", "恢复注册表", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            // Asynchronous reading of error streams + asynchronous wait for exit (with a 30-second timeout as a fallback); the UI does not freeze when importing large .reg files
            var errTask = proc.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                await proc.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"注册表恢复超时: {file.Path}");
                MessageBox.Show(this, "恢复超时，已终止 reg.exe。", "恢复注册表", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            var err = await errTask;

            if (proc.ExitCode == 0)
                MessageBox.Show(this, "已恢复。", "恢复注册表", MessageBoxButton.OK, MessageBoxImage.Information);
            else
            {
                AppLogger.Warn($"注册表恢复失败: {file.Path} {err}");
                MessageBox.Show(this, $"恢复失败：{err.Trim()}", "恢复注册表", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Warn("注册表恢复异常", ex);
            MessageBox.Show(this, $"恢复出错：{ex.Message}", "恢复注册表", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => LoadSessions();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
