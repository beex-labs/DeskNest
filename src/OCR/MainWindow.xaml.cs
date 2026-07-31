using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using WF = System.Windows.Forms;

namespace BeeX.OCR;

public partial class MainWindow : Window
{
    private readonly OcrService _ocrService = new();
    private readonly TranslationService _translationService = new();
    private readonly WF.NotifyIcon _notifyIcon;
    private BeeXTrayMenuWindow? _trayMenuWindow;
    private bool _isReallyExiting;
    private bool _trayNoticeShown;
    private bool _isBusy;
    private bool _ocrReady;
    private Exception? _ocrLoadError;
    private Task? _warmUpTask;

    public MainWindow()
    {
        InitializeComponent();
        _notifyIcon = CreateNotifyIcon();
        Loaded += (_, _) => Opacity = 1.0;
        // Title bar physical 65px: ignores DPI scaling; the screen measures a constant 65px
        Loaded += (_, _) => ApplyTitleBarPhysicalHeight();
        DpiChanged += (_, _) => ApplyTitleBarPhysicalHeight();
        LoadTranslationLanguages();
        _warmUpTask = PrepareOcrAsync();
        LogStatus("等待操作。");
    }

    private void ApplyTitleBarPhysicalHeight()
    {
        try { var scale = System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleY; if (scale > 0) TitleBarRow.Height = new GridLength(65 / scale); } catch { }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isReallyExiting)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        Task.Run(_ocrService.Dispose);
        base.OnClosing(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);

        if (e.ClickCount != 1 ||
            e.GetPosition(this).Y > 44 ||
            IsInteractive(e.OriginalSource as DependencyObject))
        {
            return;
        }

        e.Handled = true;
        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void Capture_Click(object sender, RoutedEventArgs e)
    {
        await CaptureScreenAsync();
    }

    private async void Image_Click(object sender, RoutedEventArgs e)
    {
        await RecognizeImageFileAsync();
    }

    private async void ClipboardImage_Click(object sender, RoutedEventArgs e)
    {
        await RecognizeClipboardImageAsync();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyResult(showMessage: true);
    }

    private async void Translate_Click(object sender, RoutedEventArgs e)
    {
        await TranslateResultAsync();
    }

    private void CopyTranslation_Click(object sender, RoutedEventArgs e)
    {
        CopyTranslation(showMessage: true);
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        ClearResult();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        BeeXDialog.ShowAbout(this);
    }

    private void LoadTranslationLanguages()
    {
        IReadOnlyList<TranslationLanguageOption> languages = _translationService.GetTargetLanguages();
        TargetLanguageComboBox.ItemsSource = languages;
        TargetLanguageComboBox.SelectedIndex = Math.Min(1, languages.Count - 1);
    }

    private async Task PrepareOcrAsync()
    {
        try
        {
            LogStatus("正在后台准备 PaddleOCR 引擎。");
            Stopwatch stopwatch = Stopwatch.StartNew();
            await _ocrService.LoadEngineAsync(SelectedLanguageTag());
            stopwatch.Stop();
            _ocrReady = true;
            _ocrLoadError = null;
            LogStatus("PaddleOCR 引擎已可用，用时 " + stopwatch.ElapsedMilliseconds + " ms。");
        }
        catch (Exception ex)
        {
            _ocrLoadError = ex;
            LogStatus("准备 PaddleOCR 引擎失败 - " + ex.Message);
        }
    }

    private static int GetDefaultLanguageIndex(IReadOnlyList<OcrLanguageOption> languages)
    {
        if (languages.Count <= 1)
        {
            return 0;
        }

        return 0;
    }

    private static int FindLanguageIndex(IReadOnlyList<OcrLanguageOption> languages, string languageTag)
    {
        for (int i = 0; i < languages.Count; i++)
        {
            if (string.Equals(languages[i].LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    private async Task CaptureScreenAsync()
    {
        if (!TryBeginWork(requireOcr: true))
        {
            return;
        }

        try
        {
            LogStatus("准备框选屏幕区域。");
            Hide();
            await Task.Delay(100);

            Rectangle virtualBounds = ScreenCaptureService.VirtualScreenBounds;
            var overlay = new CaptureOverlayWindow(virtualBounds);

            bool? selected = overlay.ShowDialog();

            if (selected != true || overlay.SelectedScreenBounds == null)
            {
                ShowFromTray();
                LogStatus("已取消框选。");
                return;
            }

            Rectangle selectedBounds = overlay.SelectedScreenBounds.Value;
            await Task.Delay(60);

            using Bitmap cropped = ScreenCaptureService.CaptureRegion(selectedBounds);
            ShowFromTray();
            await EnsureOcrReadyAsync();
            await RecognizeBitmapAsync(cropped, "屏幕框选");
        }
        catch (Exception ex)
        {
            ShowFromTray();
            HandleRecognitionError("框选识别失败", ex);
        }
        finally
        {
            EndWork();
        }
    }

    private async Task RecognizeImageFileAsync()
    {
        if (!TryBeginWork(requireOcr: true))
        {
            return;
        }

        try
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择要识别的图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff|PNG|*.png|JPEG|*.jpg;*.jpeg|Bitmap|*.bmp|TIFF|*.tif;*.tiff|所有文件|*.*"
            };

            if (dialog.ShowDialog(this) != true)
            {
                LogStatus("已取消图片识别。");
                return;
            }

            using Bitmap bitmap = LoadBitmap(dialog.FileName);
            await EnsureOcrReadyAsync();
            await RecognizeBitmapAsync(bitmap, Path.GetFileName(dialog.FileName));
        }
        catch (Exception ex)
        {
            HandleRecognitionError("图片识别失败", ex);
        }
        finally
        {
            EndWork();
        }
    }

    private async Task RecognizeClipboardImageAsync()
    {
        if (!TryBeginWork(requireOcr: true))
        {
            return;
        }

        try
        {
            BitmapSource? source = GetClipboardImageWithRetry();

            if (source == null)
            {
                BeeXDialog.ShowMessage(this, "剪贴板", "没有图片", "剪贴板里没有可识别的图片。");
                LogStatus("剪贴板里没有图片。");
                return;
            }

            using Bitmap bitmap = BitmapFromSource(source);
            await EnsureOcrReadyAsync();
            await RecognizeBitmapAsync(bitmap, "剪贴板图片");
        }
        catch (Exception ex)
        {
            HandleRecognitionError("剪贴板识别失败", ex);
        }
        finally
        {
            EndWork();
        }
    }

    private async Task RecognizeBitmapAsync(Bitmap bitmap, string sourceName)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        LogStatus("正在识别：" + sourceName);
        string text = await _ocrService.RecognizeAsync(bitmap, SelectedLanguageTag());
        stopwatch.Stop();

        if (string.IsNullOrWhiteSpace(text))
        {
            ResultTextBox.Clear();
            LogStatus("未识别到文字，用时 " + stopwatch.ElapsedMilliseconds + " ms。");
            BeeXDialog.ShowMessage(this, "OCR", "未识别到文字", "可以尝试放大目标区域、提高截图清晰度，或切换 OCR 语言后重试。");
            return;
        }

        ResultTextBox.Text = text;
        ResultTextBox.CaretIndex = ResultTextBox.Text.Length;
        ResultTextBox.ScrollToEnd();
        TranslationTextBox.Clear();

        if (await TryAutoCopyResultAsync(text))
        {
            LogStatus("识别完成，已复制到剪贴板，用时 " + stopwatch.ElapsedMilliseconds + " ms。");
        }
        else
        {
            LogStatus("识别完成，用时 " + stopwatch.ElapsedMilliseconds + " ms；剪贴板正忙，结果已保留在窗口。");
        }
    }

    private string? SelectedLanguageTag()
    {
        return "paddle:chinese-v5";
    }

    private string SelectedTargetLanguageCode()
    {
        return TargetLanguageComboBox.SelectedItem is TranslationLanguageOption option
            ? option.LanguageCode
            : "en";
    }

    private bool TryBeginWork(bool requireOcr)
    {
        if (_isBusy)
        {
            LogStatus("已有识别任务正在进行。");
            return false;
        }

        _isBusy = true;
        SetControlsEnabled(false);
        return true;
    }

    private async Task EnsureOcrReadyAsync()
    {
        if (_ocrReady)
        {
            return;
        }

        LogStatus("PaddleOCR 引擎仍在准备，本次操作将等待完成。");
        if (_warmUpTask != null)
        {
            await _warmUpTask;
        }

        if (!_ocrReady)
        {
            throw new InvalidOperationException(_ocrLoadError?.Message ?? "PaddleOCR 引擎未就绪。");
        }
    }

    private void EndWork()
    {
        _isBusy = false;
        SetControlsEnabled(true);
    }

    private void SetControlsEnabled(bool enabled)
    {
        CaptureButton.IsEnabled = enabled;
        ImageButton.IsEnabled = enabled;
        ClipboardImageButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
        TranslateButton.IsEnabled = enabled;
        CopyTranslationButton.IsEnabled = enabled;
        ClearButton.IsEnabled = enabled;
        TargetLanguageComboBox.IsEnabled = enabled;
    }

    private async Task TranslateResultAsync()
    {
        if (!TryBeginWork(requireOcr: false))
        {
            return;
        }

        try
        {
            string text = ResultTextBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                LogStatus("没有可翻译的识别结果。");
                BeeXDialog.ShowMessage(this, "翻译", "没有可翻译的内容", "请先完成一次 OCR 识别。");
                return;
            }

            Stopwatch stopwatch = Stopwatch.StartNew();
            LogStatus("正在翻译。");
            string translation = await _translationService.TranslateAsync(text, SelectedTargetLanguageCode());
            stopwatch.Stop();

            if (string.IsNullOrWhiteSpace(translation))
            {
                LogStatus("未获得译文。");
                BeeXDialog.ShowMessage(this, "翻译", "未获得译文", "翻译服务没有返回可用内容。");
                return;
            }

            TranslationTextBox.Text = translation;
            TranslationTextBox.CaretIndex = TranslationTextBox.Text.Length;
            TranslationTextBox.ScrollToEnd();
            LogStatus("翻译完成，用时 " + stopwatch.ElapsedMilliseconds + " ms。");
        }
        catch (Exception ex)
        {
            LogStatus("翻译失败 - " + ex.Message);
            BeeXDialog.ShowMessage(this, "翻译失败", "翻译失败", ex.Message);
        }
        finally
        {
            EndWork();
        }
    }

    private void CopyResult(bool showMessage)
    {
        string text = ResultTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            LogStatus("没有可复制的识别结果。");
            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制结果", "没有可复制的结果", "请先完成一次 OCR 识别。");
            }

            return;
        }

        try
        {
            SetClipboardText(text);
            LogStatus("识别结果已复制到剪贴板。");

            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制结果", "已复制", "识别结果已复制到剪贴板。");
            }
        }
        catch (InvalidOperationException ex)
        {
            LogStatus("复制结果失败 - " + ex.Message);

            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制结果", "复制失败", ex.Message);
            }
        }
    }

    private void CopyTranslation(bool showMessage)
    {
        string text = TranslationTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            LogStatus("没有可复制的译文。");
            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制译文", "没有可复制的译文", "请先完成一次翻译。");
            }

            return;
        }

        try
        {
            SetClipboardText(text);
            LogStatus("译文已复制到剪贴板。");

            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制译文", "已复制", "译文已复制到剪贴板。");
            }
        }
        catch (InvalidOperationException ex)
        {
            LogStatus("复制译文失败 - " + ex.Message);

            if (showMessage)
            {
                BeeXDialog.ShowMessage(this, "复制译文", "复制失败", ex.Message);
            }
        }
    }

    private void ClearResult()
    {
        ResultTextBox.Clear();
        TranslationTextBox.Clear();
        LogStatus("已清空识别结果和译文。");
    }

    private void HandleRecognitionError(string title, Exception ex)
    {
        LogStatus(title + " - " + ex.Message);
        BeeXDialog.ShowMessage(this, title, title, ex.Message);
    }

    private void LogStatus(string message)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;

        if (LogTextBox.Text.Length > 0)
        {
            LogTextBox.AppendText(Environment.NewLine);
        }

        LogTextBox.AppendText(line);
        LogTextBox.ScrollToEnd();
    }

    private WF.NotifyIcon CreateNotifyIcon()
    {
        var notifyIcon = new WF.NotifyIcon
        {
            Text = "BeeX_OCR",
            Icon = GetTrayIcon(),
            Visible = true
        };
        notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == WF.MouseButtons.Left)
            {
                Dispatcher.Invoke(ShowFromTray);
            }
            else if (e.Button == WF.MouseButtons.Right)
            {
                Dispatcher.Invoke(ShowTrayMenu);
            }
        };

        return notifyIcon;
    }

    private void ShowTrayMenu()
    {
        _trayMenuWindow?.Close();
        _trayMenuWindow = new BeeXTrayMenuWindow(
            () =>
            {
                ShowFromTray();
                return Task.CompletedTask;
            },
            async () => await CaptureScreenAsync(),
            async () => await RecognizeClipboardImageAsync(),
            async () => await TranslateResultAsync(),
            () =>
            {
                CopyResult(showMessage: false);
                return Task.CompletedTask;
            },
            () =>
            {
                CopyTranslation(showMessage: false);
                return Task.CompletedTask;
            },
            () =>
            {
                ClearResult();
                return Task.CompletedTask;
            },
            () =>
            {
                ExitFromTray();
                return Task.CompletedTask;
            });
        _trayMenuWindow.Closed += (_, _) => _trayMenuWindow = null;
        _trayMenuWindow.ShowAtCursor();
    }

    private static System.Drawing.Icon GetTrayIcon()
    {
        string? executablePath = Process.GetCurrentProcess().MainModule?.FileName;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            string[] args = Environment.GetCommandLineArgs();
            executablePath = args.Length > 0 ? args[0] : string.Empty;
        }

        return System.Drawing.Icon.ExtractAssociatedIcon(executablePath) ?? System.Drawing.SystemIcons.Application;
    }

    private void HideToTray()
    {
        LogStatus("已最小化到右下角托盘。");

        if (!_trayNoticeShown)
        {
            _trayNoticeShown = true;
            BeeXDialog.ShowMessage(
                this,
                "已缩放到右下角",
                "已缩放到右下角",
                "窗口会隐藏到任务栏右下角。\n右键 BeeX 图标可打开窗口、框选识别、识别剪贴板图片、翻译结果、复制文本或退出。");
        }

        Hide();
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        if (_isReallyExiting)
        {
            return;
        }

        bool exit = BeeXDialog.ShowConfirm(
            this,
            "退出",
            "退出 BeeX_OCR？",
            "退出后托盘入口会关闭，当前识别结果不会保存。",
            "退出",
            "取消");

        if (!exit)
        {
            return;
        }

        _isReallyExiting = true;
        _notifyIcon.Visible = false;
        Dispatcher.BeginInvoke(new Action(Close), DispatcherPriority.Background);
    }

    private static Bitmap LoadBitmap(string path)
    {
        using var source = new Bitmap(path);
        return new Bitmap(source);
    }

    private static Bitmap BitmapFromSource(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        try
        {
            for (int y = 0; y < height; y++)
            {
                Marshal.Copy(pixels, y * stride, IntPtr.Add(data.Scan0, y * data.Stride), stride);
            }
        }
        finally
        {
            bitmap.UnlockBits(data);
        }

        return bitmap;
    }

    private static void SetClipboardText(string text)
    {
        NativeClipboard.SetText(text);
    }

    private static async Task<bool> TryAutoCopyResultAsync(string text)
    {
        return await Task.Run(() => NativeClipboard.TrySetText(text, attempts: 10, delayMilliseconds: 50, out _));
    }

    private static BitmapSource? GetClipboardImageWithRetry()
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                if (!System.Windows.Clipboard.ContainsImage())
                {
                    return null;
                }

                BitmapSource? source = System.Windows.Clipboard.GetImage();
                source?.Freeze();
                return source;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or System.Runtime.InteropServices.ExternalException or InvalidOperationException)
            {
                lastError = ex;
                System.Threading.Thread.Sleep(100);
            }
        }

        throw new InvalidOperationException("剪贴板正忙，无法读取图片。请稍后重试。", lastError);
    }

    private static bool IsInteractive(DependencyObject? current)
    {
        while (current != null)
        {
            if (current is ButtonBase or TextBox or ComboBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
