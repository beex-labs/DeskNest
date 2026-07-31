using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using MessageBox = System.Windows.MessageBox;

namespace BeeXCleaner.Views;

public partial class OrphanWindow : Window
{
    private readonly OrphanScanner _scanner = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _deepDone;
    private bool _busy;

    public ObservableCollection<ResidualItem> Items { get; } = new();

    public OrphanWindow()
    {
        InitializeComponent();
        OrphanGrid.ItemsSource = Items;
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在扫描遗留项…");
        try
        {
            var found = await Task.Run(() => _scanner.Scan(deep: false));
            MergeInto(found);
            ScanInfo.Text = $"共发现 {Items.Count} 项遗留记录";
        }
        finally
        {
            // The mask must also be cleared when an exception occurs; otherwise, the window will remain unresponsive permanently after an `async void` exception is thrown.
            SetBusy(false);
        }
        UpdateStatus();
    }

    private async void OnDeepScan(object sender, RoutedEventArgs e)
    {
        if (_deepDone)
        {
            MessageBox.Show(this, "已执行过深度扫描。", "深度扫描", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, "正在深度扫描（计划任务/服务/PATH/防火墙/文件关联）…");
        try
        {
            var found = await Task.Run(() => _scanner.Scan(deep: true));
            var added = MergeInto(found);
            _deepDone = true;
            ScanInfo.Text = $"共发现 {Items.Count} 项遗留记录（深度扫描新增 {added} 项）";
        }
        finally
        {
            SetBusy(false);
        }
        UpdateStatus();
    }

    /// <summary>Merge the scan results into a list (removing duplicates based on type:path:value name).</summary>
    private int MergeInto(IEnumerable<ResidualItem> found)
    {
        var added = 0;
        foreach (var item in found)
        {
            var key = $"{item.Type}:{item.Path}:{item.RegistryValueName}";
            if (!_seen.Add(key)) continue;
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
            added++;
        }
        return added;
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidualItem.IsSelected))
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = Items.Count(i => i.IsSelected);
        StatusInfo.Text = Items.Count == 0
            ? "未发现遗留项，系统很干净 ✔"
            : $"已勾选 {selected} / {Items.Count} 项";
    }

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var i in Items) i.IsSelected = true;
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        foreach (var i in Items) i.IsSelected = false;
    }

    private async void OnClean(object sender, RoutedEventArgs e)
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(this, "请先勾选要清理的项。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var secure = SecureErase.IsChecked == true;
        var kill = KillProcesses.IsChecked == true;
        var confirm = MessageBox.Show(this,
            $"确定要永久删除选中的 {selected.Count} 项遗留记录吗？此操作不可恢复。"
            + "\n\n清理注册表前会自动导出 .reg 备份。"
            + (secure ? "\n已启用【安全擦除】：先用随机字节覆盖文件内容再删除，速度会明显变慢。" : "")
            + (kill ? "\n已启用【结束占用进程】：可能关闭正在运行的非系统程序，请先保存数据。" : ""),
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, secure ? "正在安全擦除并清理…" : "正在清理…");
        var session = new CleanupSession(CleanupOperation.Orphan);
        ResidualCleanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Clean(selected, secure, session, kill));
            // Align with the log summary format for residual cleanup: Also record the amount of space freed
            var summary = $"成功 {result.Deleted}，失败 {result.Failed}，重启后删除 {result.PendingReboot}，释放 {InstalledProgram.FormatSize(result.FreedBytes)}";
            result.LogPath = session.Flush(summary);
        }
        finally
        {
            SetBusy(false);
        }

        // Failed items remain in the list: They are kept there for review and retry; only successful items and those scheduled for deletion are removed.
        var failedSet = new HashSet<string>(result.FailedItems, StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (failedSet.Contains(item.Path)) continue;
            item.PropertyChanged -= OnItemChanged;
            Items.Remove(item);
        }
        UpdateStatus();

        new ResultWindow(result, "遗留清理结果") { Owner = this }.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Scanning/cleaning in progress. Block all ways to close the app (including the "X" in the title bar); otherwise, irreversible deletion will occur and the process will continue in the background.
    /// It cannot be canceled, and if a results window is displayed with the closed window as the owner after completion, an exception will be thrown and the results will be lost.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_busy) e.Cancel = true;
        base.OnClosing(e);
    }

    private void SetBusy(bool busy, string? text = null)
    {
        _busy = busy;
        if (text is not null) BusyText.Text = text;
        BusyOverlay.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }
}
