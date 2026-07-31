using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfImage = System.Windows.Controls.Image;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using VerticalAlignment = System.Windows.VerticalAlignment;
using Brush = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using UniformGrid = System.Windows.Controls.Primitives.UniformGrid;

namespace BeeX.DeskNest;

// Window Transparency Assistant Pop-up: Select any window and adjust its overall transparency; can be minimized to the system tray (Open Window / Restore All / Exit).
public sealed class WindowTransparencyWindow : Window
{
    readonly DeskNestService service;
    readonly WindowTransparencyService transparency;
    Forms.NotifyIcon? tray;
    Border rootBorder = null!;
    TextBlock statusText = null!;
    TextBlock opacityValue = null!;
    Slider opacitySlider = null!;
    WpfButton selectButton = null!;
    IntPtr targetWindow = IntPtr.Zero;
    IntPtr lastAppliedWindow = IntPtr.Zero;
    bool shuttingDown;
    bool buildingTheme;
    string builtTheme = "";

    public WindowTransparencyWindow(DeskNestService service, WindowTransparencyService transparency)
    {
        this.service = service;
        this.transparency = transparency;
        Title = L("視窗透明", "窗口透明", "Window transparency");
        Width = 430;
        Height = 452;
        MinWidth = 380;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.CanResize;
        Background = WpfBrushes.Transparent;
        ShowInTaskbar = true;
        BuildContent();
        SourceInitialized += (_, _) => WindowRegionHelper.ApplyDeferred(this, service.State.CornerRadius);
        SizeChanged += (_, _) => WindowRegionHelper.ApplyDeferred(this, service.State.CornerRadius);
    }

    string L(string tw, string cn, string en) => service.State.Language == "zh-CN" ? cn : service.State.Language == "en-US" ? en : tw;
    bool Dark => service.State.Theme == "Dark";
    bool Honey => service.State.Theme == "Honey";

    void BuildContent()
    {
        builtTheme = service.State.Theme;
        var surface = Dark ? WpfColor.FromRgb(22, 29, 45) : Honey ? WpfColor.FromRgb(255, 244, 222) : WpfColor.FromRgb(250, 251, 252);
        var foreground = Dark ? WpfBrushes.White : new SolidColorBrush(WpfColor.FromRgb(13, 19, 33));
        var muted = new SolidColorBrush(Dark ? WpfColor.FromRgb(184, 192, 207) : WpfColor.FromRgb(107, 114, 128));
        Foreground = foreground;
        FontFamily = new System.Windows.Media.FontFamily(service.InterfaceFontFamily());

        rootBorder = new Border { Background = new SolidColorBrush(surface), BorderBrush = new SolidColorBrush(WpfColor.FromArgb(120, 255, 138, 0)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(service.State.CornerRadius), ClipToBounds = true };
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarMetrics.Dip(this)) });
        grid.RowDefinitions.Add(new RowDefinition());

        // Title Bar
        var titleBar = new Grid { Margin = new Thickness(14, 0, 6, 0) };
        titleBar.ColumnDefinitions.Add(new ColumnDefinition());
        titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var brand = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new WpfImage { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")), Width = 20, Height = 20 });
        brand.Children.Add(new TextBlock { Text = L("視窗透明", "窗口透明", "Window transparency"), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold, Foreground = foreground });
        titleBar.Children.Add(brand);
        var caption = new StackPanel { Orientation = WpfOrientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        caption.Children.Add(CaptionButton("−", foreground, HideToTray));
        caption.Children.Add(CaptionButton("×", foreground, HideToTray));
        Grid.SetColumn(caption, 1);
        titleBar.Children.Add(caption);
        titleBar.MouseLeftButtonDown += (_, e) => { if (e.LeftButton == MouseButtonState.Pressed && !InputHitTestHelper.IsInteractive(e.OriginalSource as DependencyObject)) try { DragMove(); } catch { } };
        grid.Children.Add(titleBar);

        // Content Area
        var body = new Grid { Margin = new Thickness(28, 12, 28, 22) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        body.Children.Add(new TextBlock { Text = L("先選取視窗，再調整透明度。255 為完全不透明。", "先选取窗口，再调整透明度。255 为完全不透明。", "Select a window, then adjust opacity. 255 is fully opaque."), Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

        var opacityRow = new Grid();
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition());
        opacityRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        opacityRow.Children.Add(new TextBlock { Text = L("透明度", "透明度", "Opacity"), FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground });
        opacityValue = new TextBlock { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = foreground };
        Grid.SetColumn(opacityValue, 1);
        opacityRow.Children.Add(opacityValue);
        Grid.SetRow(opacityRow, 1);
        body.Children.Add(opacityRow);

        opacitySlider = new Slider { Minimum = 40, Maximum = 255, Value = 200, Margin = new Thickness(0, 6, 0, 10) };
        opacitySlider.ValueChanged += (_, _) => OnOpacityChanged();
        Grid.SetRow(opacitySlider, 2);
        body.Children.Add(opacitySlider);

        var statusBorder = new Border { Background = new SolidColorBrush(WpfColor.FromArgb(Dark ? (byte)40 : (byte)120, 255, 255, 255)), CornerRadius = new CornerRadius(9), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 2, 0, 12), VerticalAlignment = VerticalAlignment.Stretch };
        statusText = new TextBlock { Foreground = foreground, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Top, FontSize = 12.5 };
        statusBorder.Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = statusText };
        Grid.SetRow(statusBorder, 3);
        body.Children.Add(statusBorder);

        var buttons = new UniformGrid { Columns = 4, Rows = 1, Margin = new Thickness(-3, 0, -3, 0) };
        selectButton = ToolButton(L("選取視窗", "选取窗口", "Select"), false, async () => await SelectWindowAsync());
        buttons.Children.Add(selectButton);
        buttons.Children.Add(ToolButton(L("套用", "套用", "Apply"), true, ApplyOpacity));
        buttons.Children.Add(ToolButton(L("還原最近", "还原最近", "Restore recent"), false, RestoreRecent));
        buttons.Children.Add(ToolButton(L("還原全部", "还原全部", "Restore all"), false, () => RestoreAll(true)));
        Grid.SetRow(buttons, 4);
        body.Children.Add(buttons);

        var hint = new TextBlock { Text = L("Alt + X 一鍵最小化所有已透明化的視窗", "Alt + X 一键最小化所有已透明化的窗口", "Alt + X minimizes every window you made transparent"), Foreground = muted, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(hint, 5);
        body.Children.Add(hint);

        Grid.SetRow(body, 1);
        grid.Children.Add(body);
        rootBorder.Child = grid;
        Content = rootBorder;

        UpdateOpacityLabel();
        SetStatus(L("等待操作。", "等待操作。", "Ready."));
    }

    WpfButton CaptionButton(string glyph, Brush foreground, Action click)
    {
        var button = new WpfButton { Content = glyph, Width = 40, Height = 40, Padding = new Thickness(0), Margin = new Thickness(0), Background = WpfBrushes.Transparent, Foreground = foreground, FontFamily = new System.Windows.Media.FontFamily("Segoe UI Symbol"), FontSize = glyph == "×" ? 15 : 16, Cursor = Cursors.Hand };
        button.Click += (_, _) => click();
        return button;
    }

    WpfButton ToolButton(string text, bool primary, Action click)
    {
        var button = new WpfButton { Content = text, Height = 40, Margin = new Thickness(3, 0, 3, 0), Cursor = Cursors.Hand };
        if (primary) { button.Background = new SolidColorBrush(WpfColor.FromRgb(255, 138, 0)); button.Foreground = WpfBrushes.White; }
        else if (Dark) { button.Background = new SolidColorBrush(WpfColor.FromArgb(45, 255, 255, 255)); button.Foreground = WpfBrushes.White; }
        button.Click += (_, _) => click();
        return button;
    }

    public void ShowTool()
    {
        EnsureTray();
        if (builtTheme != service.State.Theme) ApplyTheme();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    // After switching themes, rebuild the content while preserving the current transparency slider value, so that the Window Transparency tool matches the DeskNest theme colors.
    public void ApplyTheme()
    {
        var prev = opacitySlider?.Value ?? 200;
        var prevTarget = targetWindow;
        buildingTheme = true;
        BuildContent();
        if (opacitySlider != null)
        {
            opacitySlider.Value = prev;
            UpdateOpacityLabel();
        }
        buildingTheme = false;
        targetWindow = prevTarget;
        Opacity = Math.Max(0.3, prev / 255.0);
    }

    void HideToTray()
    {
        EnsureTray();
        Hide();
        SetStatus(L("已最小化到系統匣，透明效果會保留。", "已最小化到系统托盘，透明效果会保留。", "Minimized to tray; transparency stays active."));
    }

    void EnsureTray()
    {
        if (tray != null) { tray.Visible = true; return; }
        tray = new Forms.NotifyIcon { Icon = App.CreateTrayIcon(), Text = L("視窗透明", "窗口透明", "Window transparency"), Visible = true };
        tray.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowTool);
            else if (e.Button == Forms.MouseButtons.Right) Dispatcher.Invoke(ShowTrayMenu);
        };
    }

    void ShowTrayMenu()
    {
        var menu = new WpfContextMenu();
        StyleMenu(menu);
        WpfMenuItem Item(string text, Action action, bool danger = false)
        {
            var item = new WpfMenuItem { Header = text };
            if (danger) item.Foreground = new SolidColorBrush(WpfColor.FromRgb(217, 45, 32));
            item.Click += (_, _) => { menu.IsOpen = false; action(); };
            return item;
        }
        menu.Items.Add(Item(L("打開視窗", "打开窗口", "Open window"), ShowTool));
        menu.Items.Add(Item(L("還原全部", "还原全部", "Restore all"), () => { ShowTool(); RestoreAll(true); }));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(L("退出", "退出", "Exit"), ExitTool, true));
        service.ShowTrayContextMenu(menu);
    }

    void StyleMenu(WpfContextMenu menu)
    {
        menu.Background = Dark ? new SolidColorBrush(WpfColor.FromRgb(22, 29, 45)) : Honey ? new SolidColorBrush(WpfColor.FromRgb(255, 244, 222)) : new SolidColorBrush(WpfColor.FromRgb(250, 251, 252));
        menu.Foreground = Dark ? WpfBrushes.White : new SolidColorBrush(WpfColor.FromRgb(13, 19, 33));
        menu.BorderBrush = new SolidColorBrush(WpfColor.FromArgb(120, 255, 138, 0));
    }

    // Exit the Transparency Tool: Restore all windows, hide windows, and hide system tray icons (this does not affect the DeskNest main program).
    void ExitTool()
    {
        RestoreAll(false);
        Hide();
        if (tray != null) tray.Visible = false;
    }

    public void ShutdownTool()
    {
        shuttingDown = true;
        try { RestoreAll(false); } catch { }
        if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
    }

    void UpdateOpacityLabel()
    {
        var value = (int)Math.Round(opacitySlider.Value);
        var percent = (int)Math.Round(value / 255.0 * 100);
        opacityValue.Text = $"{value} / 255  ·  {percent}%";
    }

    // Use the tool window's own transparency to preview changes in real time as you drag the slider; changes are not automatically applied to the target window, regardless of whether a target has been selected.
    // Users must click "Apply" for the changes to take effect. This is not triggered during theme rebuilding (to prevent residual `targetWindow` values from being applied incorrectly).
    void OnOpacityChanged()
    {
        if (buildingTheme || opacitySlider == null || opacityValue == null) return;
        UpdateOpacityLabel();
        Opacity = Math.Max(0.3, Math.Round(opacitySlider.Value) / 255.0);
    }

    void SetStatus(string message) => statusText.Text = $"[{DateTime.Now:HH:mm:ss}] {message}";

    async Task SelectWindowAsync()
    {
        selectButton.IsEnabled = false;
        try
        {
            for (var seconds = 3; seconds >= 1; seconds--)
            {
                SetStatus(L($"請把鼠標移到目標視窗上，{seconds} 秒後自動選取。", $"请把鼠标移到目标窗口上，{seconds} 秒后自动选取。", $"Move the mouse over the target window; selecting in {seconds}s."));
                await Task.Delay(1000);
            }
            Hide();
            await Task.Delay(400);
            var root = WindowTransparencyService.GetRootWindowUnderCursor();
            var self = new WindowInteropHelper(this).Handle;
            if (root == self) throw new InvalidOperationException(L("不能選擇本工具自己。", "不能选择本工具自己。", "Cannot select this tool itself."));
            targetWindow = root;
            var title = WindowTransparencyService.GetWindowTitle(root);
            var process = WindowTransparencyService.GetProcessName(root);
            if (string.IsNullOrWhiteSpace(title)) title = L("沒有標題的視窗", "没有标题的窗口", "Untitled window");
            SetStatus(L($"已選取：{process} | {title}。調整滑桿後按「套用」。", $"已选取：{process} | {title}。调整滑杆后按「套用」。", $"Selected: {process} | {title}. Adjust the slider, then Apply."));
        }
        catch (Exception ex)
        {
            targetWindow = IntPtr.Zero;
            SetStatus(L("選取失敗 - ", "选取失败 - ", "Select failed - ") + ex.Message);
        }
        finally
        {
            Show();
            Activate();
            selectButton.IsEnabled = true;
        }
    }

    bool TryGetTarget(out IntPtr hwnd)
    {
        hwnd = targetWindow;
        if (hwnd != IntPtr.Zero) return true;
        SetStatus(L("尚未選取視窗，請先按「選取視窗」。", "尚未选取窗口，请先按「选取窗口」。", "No window selected. Click Select first."));
        return false;
    }

    void ApplyOpacity()
    {
        if (!TryGetTarget(out var hwnd)) return;
        try
        {
            var alpha = (byte)Math.Round(opacitySlider.Value);
            transparency.ApplyOpacity(hwnd, alpha);
            var percent = (int)Math.Round(alpha / 255.0 * 100);
            SetStatus(L($"透明度已套用，約 {percent}%（Alpha {alpha}）。若要調整請重新選取視窗。", $"透明度已套用，约 {percent}%（Alpha {alpha}）。若要调整请重新选取窗口。", $"Applied ~{percent}% (Alpha {alpha}). Select another window or re-select to adjust."));
            lastAppliedWindow = hwnd;
            targetWindow = IntPtr.Zero;
            Opacity = 1;
        }
        catch (Exception ex)
        {
            SetStatus(L("套用失敗 - ", "套用失败 - ", "Apply failed - ") + ex.Message);
        }
    }

    void RestoreRecent()
    {
        if (lastAppliedWindow == IntPtr.Zero)
        {
            SetStatus(L("沒有可還原的記錄。請先選取視窗並點擊「套用」。", "没有可还原的记录。请先选取窗口并点击「套用」。", "Nothing to restore. Select a window and click Apply first."));
            return;
        }
        var hwnd = lastAppliedWindow;
        lastAppliedWindow = IntPtr.Zero;
        targetWindow = IntPtr.Zero;
        var title = WindowTransparencyService.GetWindowTitle(hwnd);
        var restored = transparency.RestoreWindow(hwnd);
        if (restored)
            SetStatus(L($"已還原最近套用的視窗「{title}」。", $"已还原最近套用的窗口「{title}」。", $"Restored most recent window \"{title}\"."));
        else
            SetStatus(L("還原失敗，視窗可能已關閉或尚未套用過透明度。", "还原失败，窗口可能已关闭或尚未套用过透明度。", "Restore failed; the window may have been closed or wasn't modified."));
        Opacity = 1;
    }

    void RestoreAll(bool report)
    {
        var count = transparency.RestoreAllWindows();
        if (report) SetStatus(L($"已還原 {count} 個視窗。", $"已还原 {count} 个窗口。", $"Restored {count} window(s)."));
        lastAppliedWindow = IntPtr.Zero;
        targetWindow = IntPtr.Zero;
        Opacity = 1;
    }

    // The Grid background in the title bar is blank and is not included in the click-through test; instead, use the window-level Preview: The top title bar (physical 65px) is a non-interactive area; hold down to drag the entire window.
    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (e.ClickCount != 1 || e.GetPosition(this).Y > TitleBarMetrics.Dip(this) || InputHitTestHelper.IsInteractive(e.OriginalSource as DependencyObject)) return;
        e.Handled = true;
        try { DragMove(); } catch { }
    }

    /// <summary>DPI changes after dragging across screens; recalculating the logical height corresponding to the physical 65px in the title bar</summary>
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (rootBorder?.Child is Grid grid && grid.RowDefinitions.Count > 0) grid.RowDefinitions[0].Height = new GridLength(TitleBarMetrics.Dip(this));
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // The Close button/Alt+F4 only minimizes the window to the system tray (while retaining the transparency effect); the program is not truly closed until the DeskNest main application is exited.
        if (shuttingDown) { base.OnClosing(e); return; }
        e.Cancel = true;
        HideToTray();
    }
}
