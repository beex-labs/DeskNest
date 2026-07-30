using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using SaveFileDialog = Microsoft.Win32.SaveFileDialog;

namespace BeeXCleaner.Views;

public partial class ResidualWindow : Window
{
    private readonly IReadOnlyList<InstalledProgram> _programs;
    private readonly ResidualScanner _scanner = new();
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private bool _deepDone;
    private bool _busy;

    public ObservableCollection<ResidualItem> Items { get; } = new();

    public ResidualWindow(IReadOnlyList<InstalledProgram> programs)
    {
        InitializeComponent();
        _programs = programs;
        ResidualGrid.ItemsSource = Items;
        Loaded += OnLoadedAsync;
    }

    private async void OnLoadedAsync(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "正在扫描残留…");
        try
        {
            var found = await Task.Run(() => ScanPrograms(ScanMode.Standard));
            MergeInto(found);

            var names = _programs.Count == 1 ? $"“{_programs[0].DisplayName}”" : $"{_programs.Count} 个程序";
            ScanInfo.Text = $"为 {names} 找到 {Items.Count} 项残留";
        }
        finally
        {
            // 异常时也必须解除遮罩，否则 async void 抛出后窗口永久不可操作
            SetBusy(false);
        }
        UpdateStatus();
    }

    private List<ResidualItem> ScanPrograms(ScanMode mode)
    {
        var all = new List<ResidualItem>();
        foreach (var p in _programs)
            all.AddRange(_scanner.Scan(p, mode));
        return all
            .OrderBy(i => i.Type)
            .ThenBy(i => i.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>把扫描/手动结果并入列表（按 类型:路径:值名 去重）。</summary>
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

    private async void OnDeepScan(object sender, RoutedEventArgs e)
    {
        if (_deepDone)
        {
            MessageBox.Show(this, "已执行过深度扫描。", "深度扫描", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        SetBusy(true, "正在深度扫描（扩大名称匹配范围）…");
        try
        {
            var found = await Task.Run(() => ScanPrograms(ScanMode.Deep));
            var added = MergeInto(found);
            _deepDone = true;
            DeepHint.Text = $"深度扫描完成，新增 {added} 项（低置信项默认不勾选）";
        }
        finally
        {
            SetBusy(false);
        }
        UpdateStatus();
    }

    private async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "选择要加入清理清单的文件夹", Multiselect = false };
        if (dlg.ShowDialog(this) != true) return;
        // 目录大小需递归实测，大目录耗时无上界，必须移出 UI 线程
        SetBusy(true, "正在计算目录大小…");
        ResidualItem item;
        try
        {
            item = await Task.Run(() => ResidualScanner.CreateManualFolder(dlg.FolderName));
        }
        finally
        {
            SetBusy(false);
        }
        if (MergeInto(new[] { item }) == 0)
            MessageBox.Show(this, "该目录已在清单中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateStatus();
    }

    private void OnAddRegistry(object sender, RoutedEventArgs e)
    {
        var input = PromptInput("手动添加注册表项",
            "输入完整注册表路径（HKEY_CURRENT_USER\\... 或 HKEY_LOCAL_MACHINE\\...）：");
        if (string.IsNullOrWhiteSpace(input)) return;
        if (!input.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase)
            && !input.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(this, "仅支持 HKEY_CURRENT_USER 或 HKEY_LOCAL_MACHINE 根。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // 拦截根级/系统关键位置：整键删除会损坏系统或抹掉大量软件注册信息。
        if (ResidualScanner.IsProtectedRegistryRoot(input))
        {
            MessageBox.Show(this,
                "该注册表路径为系统/软件根级位置，整键删除会损坏系统或抹掉大量软件注册信息，已禁止添加。\n\n"
                + "请改为具体的软件子键（如 HKEY_CURRENT_USER\\SOFTWARE\\发行商\\产品）。",
                "禁止添加", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (MergeInto(new[] { ResidualScanner.CreateManualRegistry(input) }) == 0)
            MessageBox.Show(this, "该注册表项已在清单中。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
        UpdateStatus();
    }

    private void OnExportList(object sender, RoutedEventArgs e)
    {
        if (Items.Count == 0)
        {
            MessageBox.Show(this, "当前没有可导出的项。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new SaveFileDialog
        {
            Title = "导出残留清单",
            Filter = "文本文件 (*.txt)|*.txt",
            FileName = $"BeeXCleaner-残留清单-{DateTime.Now:yyyyMMdd-HHmmss}.txt"
        };
        if (dlg.ShowDialog(this) != true) return;

        var sb = new StringBuilder();
        sb.AppendLine($"BeeX Cleaner 残留清单 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"目标: {string.Join(", ", _programs.Select(p => p.DisplayName))}");
        sb.AppendLine(new string('-', 60));
        foreach (var i in Items)
            sb.AppendLine($"[{(i.IsSelected ? "√" : " ")}] {i.TypeDisplay}/{i.RiskDisplay}/{i.ConfidenceDisplay}  {i.Path}  ← {i.MatchReason}");
        try
        {
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            MessageBox.Show(this, "已导出清单。", "导出", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "导出", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResidualItem.IsSelected))
            UpdateStatus();
    }

    private void UpdateStatus()
    {
        var selected = Items.Count(i => i.IsSelected);
        var size = Items.Where(i => i.IsSelected).Sum(i => i.SizeBytes);
        StatusInfo.Text = Items.Count == 0
            ? "未发现残留，系统很干净 ✔"
            : $"已勾选 {selected} / {Items.Count} 项 · 约 {InstalledProgram.FormatSize(size)}";
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
            $"确定要永久删除选中的 {selected.Count} 项残留吗？此操作不可恢复。"
            + "\n\n清理注册表前会自动导出 .reg 备份。"
            + (secure ? "\n已启用【安全擦除】：先用随机字节覆盖文件内容再删除，速度会明显变慢。" : "")
            + (kill ? "\n已启用【结束占用进程】：可能关闭正在运行的非系统程序，请先保存数据。" : ""),
            "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetBusy(true, secure ? "正在安全擦除并清理…" : "正在清理…");
        var session = new CleanupSession(CleanupOperation.Residual, _programs.Select(p => p.DisplayName));
        ResidualCleanResult result;
        try
        {
            result = await Task.Run(() => _scanner.Clean(selected, secure, session, kill));
            var summary = $"成功 {result.Deleted}，失败 {result.Failed}，重启后删除 {result.PendingReboot}，释放 {InstalledProgram.FormatSize(result.FreedBytes)}";
            result.LogPath = session.Flush(summary);
        }
        finally
        {
            SetBusy(false);
        }

        // 失败项仍真实存在于磁盘/注册表：保留在列表中供复查与重试，
        // 只移除删除成功与已安排重启后删除的项，避免用户误以为已全部删除。
        var failedSet = new HashSet<string>(result.FailedItems, StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            if (failedSet.Contains(item.Path)) continue;
            item.PropertyChanged -= OnItemChanged;
            Items.Remove(item);
        }
        UpdateStatus();

        new ResultWindow(result, "残留清理结果") { Owner = this }.ShowDialog();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 扫描/清理进行中拦截一切关闭途径（含标题栏 X）：否则不可逆删除在后台继续、
    /// 无法取消，且完成后以已关闭窗口为 Owner 弹结果窗会抛异常、清理结果丢失。
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

    /// <summary>轻量输入框（无独立 XAML，运行期构造）。</summary>
    private string? PromptInput(string title, string prompt)
    {
        var box = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 460 };
        var ok = new System.Windows.Controls.Button
        {
            Content = "确定", Width = 76, IsDefault = true, Margin = new Thickness(0, 0, 8, 0)
        };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 76, IsCancel = true };
        var buttons = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);

        var dlg = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = System.Windows.Media.Brushes.White
        };
        ok.Click += (_, _) => { dlg.DialogResult = true; };
        return dlg.ShowDialog() == true ? box.Text.Trim() : null;
    }
}
