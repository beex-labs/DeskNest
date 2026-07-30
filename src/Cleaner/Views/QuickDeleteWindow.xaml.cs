using System.ComponentModel;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace BeeXCleaner.Views;

public partial class QuickDeleteWindow : Window
{
    private string? _target;
    private bool _isFolder;
    private bool _busy;
    private int _sizeGen; // 大小计算代际令牌：防止快速换目标后，旧目标的慢任务回写错位

    public QuickDeleteWindow() => InitializeComponent();

    private void OnPickFile(object sender, RoutedEventArgs e)
    {
        if (_busy) return; // 删除进行中禁止更换目标：避免并发删除与状态被覆盖
        var dlg = new OpenFileDialog { Title = "选择要删除的文件", CheckFileExists = true, Multiselect = false };
        if (dlg.ShowDialog(this) == true) SetTarget(dlg.FileName, isFolder: false);
    }

    private void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (_busy) return; // 同上
        var dlg = new OpenFolderDialog { Title = "选择要删除的文件夹", Multiselect = false };
        if (dlg.ShowDialog(this) == true) SetTarget(dlg.FolderName, isFolder: true);
    }

    private void SetTarget(string path, bool isFolder)
    {
        _target = path;
        _isFolder = isFolder;
        PathText.Text = $"{(isFolder ? "文件夹" : "文件")}：{path}";
        DeleteBtn.IsEnabled = true;
        SizeText.Text = "正在计算大小…";

        var gen = ++_sizeGen;
        Task.Run(() =>
        {
            long size;
            try { size = isFolder ? FileSystemUtil.DirectorySize(path) : new FileInfo(path).Length; }
            catch { size = 0; }
            Dispatcher.Invoke(() =>
            {
                // 仅当仍是当前目标的计算结果才回显，丢弃过时结果
                if (gen == _sizeGen)
                    SizeText.Text = $"大小：约 {InstalledProgram.FormatSize(size)}";
            });
        });
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_target)) return;
        var path = _target!;

        // 安全护栏：网络盘/NAS 禁止；文件夹另需通过灾难性根目录硬拦截
        if (FileSystemUtil.IsNetworkPath(path))
        {
            MessageBox.Show(this, "网络盘 / NAS 路径不允许删除，仅限本机本地磁盘。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (_isFolder && !UninstallService.IsSafeToDelete(path))
        {
            MessageBox.Show(this, "该目录为操作系统关键根目录，已被硬保护，禁止删除。", "无法删除",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 文件级系统保护（6.5）：位于 Windows / System32 / SysWOW64 的文件禁止删除
        if (!_isFolder && !UninstallService.IsSafeFileToDelete(path))
        {
            MessageBox.Show(this, "该文件位于操作系统关键目录（Windows / System32 / SysWOW64），已被保护，禁止删除。",
                "无法删除", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if ((_isFolder && !Directory.Exists(path)) || (!_isFolder && !File.Exists(path)))
        {
            MessageBox.Show(this, "目标已不存在。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            ResetAfterDelete();
            return;
        }

        // 云同步根目录警示（阶段 6）：本地路径但删除会同步到云端，不硬禁，给强提示
        if (UninstallService.IsCloudSyncRoot(path))
        {
            var cloud = MessageBox.Show(this,
                "该目录疑似云同步根目录（OneDrive / Dropbox / Google Drive）。\n删除会同步到云端，可能影响其它设备上的数据。\n\n仍要继续吗？",
                "云同步目录警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (cloud != MessageBoxResult.Yes) return;
        }

        var secure = SecureErase.IsChecked == true;
        var confirm = MessageBox.Show(this,
            $"确定要永久删除以下{(_isFolder ? "文件夹" : "文件")}吗？此操作不可恢复。\n\n{path}"
            + (secure ? "\n\n已启用【安全擦除】：将先用随机字节覆盖内容再删除，速度会明显变慢。" : ""),
            "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true);
        // 接入清理会话：与其它不可逆删除一致留下审计痕迹，可在“清理历史”中查看
        var session = new CleanupSession(CleanupOperation.QuickDelete, new[] { path });
        DeleteResult result;
        try
        {
            result = await Task.Run(() => _isFolder
                ? FileSystemUtil.ForceDeleteDirectory(path, secure)
                : FileSystemUtil.ForceDeleteFile(path, secure));
        }
        catch (Exception ex)
        {
            SetBusy(false);
            session.Log($"✗ 删除出错: {path} — {ex.Message}");
            session.Flush("失败 1");
            MessageBox.Show(this, $"删除出错：{ex.Message}", "删除", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        SetBusy(false);

        var kindText = _isFolder ? "文件夹" : "文件";
        switch (result)
        {
            case DeleteResult.Removed:
                session.Log($"✔ 删除{kindText}: {path}" + (secure ? "（安全擦除）" : ""));
                session.Flush("成功 1");
                MessageBox.Show(this, secure ? "已安全擦除并删除。" : "已删除。", "删除",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                ResetAfterDelete();
                break;
            case DeleteResult.ScheduledReboot:
                session.Log($"↻ 部分被占用/受保护，已安排重启后删除: {path}"
                    + (secure ? "（该部分未能安全擦除）" : ""));
                session.Flush("重启后删除 1");
                MessageBox.Show(this, "部分内容被占用 / 受保护，无法立即删除，已安排在系统重启后删除。"
                    + (secure ? "\n\n注意：该部分内容未经安全擦除，重启删除后数据仍可能被恢复。" : ""),
                    "删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                ResetAfterDelete();
                break;
            default:
                session.Log($"✗ 删除失败: {path}");
                session.Flush("失败 1");
                MessageBox.Show(this, "删除失败（可能被占用或权限不足）。", "删除",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                break;
        }
    }

    private void ResetAfterDelete()
    {
        _target = null;
        PathText.Text = "尚未选择。请点击上方按钮选择要删除的文件或文件夹。";
        SizeText.Text = "";
        DeleteBtn.IsEnabled = false;
        SecureErase.IsChecked = false;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        DeleteBtn.IsEnabled = !busy && _target is not null;
        Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
    }

    /// <summary>删除进行中拦截关闭（含标题栏 X）：避免不可逆删除在后台继续且结果提示丢失。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
