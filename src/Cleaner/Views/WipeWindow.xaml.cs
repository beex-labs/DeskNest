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
            // Task.Run Package: Filling Loop (Random Number Generation/Flush) and Synchronous Deletion of Hundreds of GB of Filler Files at the End
            // All operations are executed in a background thread to prevent the UI thread from being tied up by high-frequency operations; `Progress<T>` automatically returns to the UI thread.
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

        // Refresh the display of available disk space
        DriveGrid.ItemsSource = _wiper.GetWipeableDrives();

        var icon = result.Completed || result.Cancelled ? MessageBoxImage.Information : MessageBoxImage.Warning;
        MessageBox.Show(this,
            $"{result.Message}\n\n本次写入覆盖: {InstalledProgram.FormatSize(result.WrittenBytes)}",
            "空间擦除", MessageBoxButton.OK, icon);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Block all ways to close the window (including the "X" in the title bar): While erasing is in progress, cancel the task without closing the window,
    /// Prevent the background process from continuing to fill up the disk after the window closes, with no way to cancel it.
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (_running)
        {
            e.Cancel = true;
            _cts?.Cancel(); // Users should wait until the erasure task is complete (including cleaning up the fill files) before shutting down.
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
