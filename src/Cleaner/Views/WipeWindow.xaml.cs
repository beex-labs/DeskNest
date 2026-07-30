using System.ComponentModel;
using System.Threading;
using System.Windows;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using MessageBox = System.Windows.MessageBox;

namespace BeeXCleaner.Views;

public partial class WipeWindow : Window
{
    private readonly FreeSpaceWiper _wiper = new();
    private CancellationTokenSource? _cts;
    private bool _running;

    public WipeWindow()
    {
        InitializeComponent();
        DriveGrid.ItemsSource = _wiper.GetWipeableDrives();
    }

    private async void OnStart(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        if (DriveGrid.SelectedItem is not WipeDriveInfo drive)
        {
            MessageBox.Show(this, "请先在列表中选择要擦除的磁盘。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var marginText = InstalledProgram.FormatSize(FreeSpaceWiper.GetMarginBytes(drive.Root));
        var confirm = MessageBox.Show(this,
            $"将对磁盘 {drive.Display} 的可用空间（约 {drive.FreeText}）进行深度擦除。\n\n" +
            $"· 会临时占满该磁盘可用空间（保留 {marginText} 安全余量），可能耗时较长；\n" +
            "· 不会影响现有文件，只摧毁“已删除文件”的可恢复数据；\n" +
            "· 完成或取消后自动清除填充文件。\n\n是否开始？",
            "确认深度擦除", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        SetRunning(true);
        var progress = new Progress<WipeProgress>(p =>
        {
            Bar.Value = p.Fraction;
            ProgText.Text = $"{p.Fraction:P0}  已写入 {InstalledProgram.FormatSize(p.Written)} / {InstalledProgram.FormatSize(p.Target)}";
        });

        _cts = new CancellationTokenSource();
        WipeResult result;
        try
        {
            // Task.Run 包裹：填充循环（随机数生成/Flush）与结尾同步删除数百 GB 填充文件
            // 都在后台线程执行，避免 UI 线程被高频占用；Progress<T> 会自动回投 UI 线程。
            var token = _cts.Token;
            result = await Task.Run(() => _wiper.WipeAsync(drive.Root, progress, token));
        }
        catch (Exception ex)
        {
            result = new WipeResult(false, false, 0, ex.Message);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            SetRunning(false);
        }

        if (result.Completed) Bar.Value = 1;
        ProgText.Text = result.Cancelled ? "已取消" : (result.Completed ? "完成" : "未完成");

        // 刷新磁盘可用空间显示
        DriveGrid.ItemsSource = _wiper.GetWipeableDrives();

        var icon = result.Completed || result.Cancelled ? MessageBoxImage.Information : MessageBoxImage.Warning;
        MessageBox.Show(this,
            $"{result.Message}\n\n本次写入覆盖: {InstalledProgram.FormatSize(result.WrittenBytes)}",
            "空间擦除", MessageBoxButton.OK, icon);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 拦截一切关闭途径（含标题栏 X）：擦除进行中先取消任务而不关窗，
    /// 避免窗口消失后后台继续把磁盘写满且无处可取消。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            _cts?.Cancel(); // 等擦除任务收尾（清理填充文件）后用户再关闭
        }
        base.OnClosing(e);
    }

    private void SetRunning(bool running)
    {
        _running = running;
        StartBtn.IsEnabled = !running;
        CancelBtn.IsEnabled = running;
        DriveGrid.IsEnabled = !running;
    }
}
