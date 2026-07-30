using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WF = System.Windows.Forms;

namespace BeeX.OCR;

internal sealed class BeeXTrayMenuWindow : Window
{
    private const double MenuWidth = 236;
    private const double EstimatedHeight = 361;
    private const double WindowPadding = 2;
    private const double ItemWidth = 204;
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(250, 251, 252));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(13, 19, 33));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
    private static readonly Brush OrangeBrush = new SolidColorBrush(Color.FromRgb(255, 138, 0));
    private static readonly Brush SeparatorBrush = new SolidColorBrush(Color.FromRgb(214, 218, 225));

    public BeeXTrayMenuWindow(
        Func<Task> openWindow,
        Func<Task> captureScreen,
        Func<Task> recognizeClipboard,
        Func<Task> translateResult,
        Func<Task> copyResult,
        Func<Task> copyTranslation,
        Func<Task> clearResult,
        Func<Task> exitApp)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = false;

        var stack = new StackPanel();
        stack.Children.Add(CreateItem("打开窗口", TextBrush, openWindow));
        stack.Children.Add(CreateItem("框选识别", TextBrush, captureScreen));
        stack.Children.Add(CreateItem("识别剪贴板图片", TextBrush, recognizeClipboard));
        stack.Children.Add(CreateItem("翻译结果", TextBrush, translateResult));
        stack.Children.Add(CreateItem("复制结果", TextBrush, copyResult));
        stack.Children.Add(CreateItem("复制译文", TextBrush, copyTranslation));
        stack.Children.Add(CreateItem("清空结果", TextBrush, clearResult));
        stack.Children.Add(CreateSeparator());
        stack.Children.Add(CreateItem("退出", DangerBrush, exitApp));

        Content = new Border
        {
            Width = MenuWidth,
            Margin = new Thickness(WindowPadding),
            Padding = new Thickness(12, 14, 12, 12),
            Background = BackgroundBrush,
            BorderBrush = OrangeBrush,
            BorderThickness = new Thickness(1.25),
            CornerRadius = new CornerRadius(16),
            Child = stack
        };

        Deactivated += (_, _) => Close();
    }

    public void ShowAtCursor()
    {
        System.Drawing.Point cursor = WF.Cursor.Position;
        System.Drawing.Rectangle workArea = WF.Screen.FromPoint(cursor).WorkingArea;

        double width = MenuWidth + WindowPadding * 2;
        double height = EstimatedHeight + WindowPadding * 2;
        Left = Math.Min(cursor.X, workArea.Right - width - 6);
        Top = cursor.Y - height;

        if (Top < workArea.Top + 6)
        {
            Top = cursor.Y;
        }

        if (Top + height > workArea.Bottom - 6)
        {
            Top = workArea.Bottom - height - 6;
        }

        Show();
        Activate();
    }

    private static FrameworkElement CreateSeparator()
    {
        return new Border
        {
            Height = 14,
            Margin = new Thickness(8, 0, 8, 0),
            Child = new Border
            {
                Height = 1,
                VerticalAlignment = VerticalAlignment.Center,
                Background = SeparatorBrush
            }
        };
    }

    private static FrameworkElement CreateItem(string text, Brush normalBrush, Func<Task> action)
    {
        var label = new TextBlock
        {
            Text = text,
            Foreground = normalBrush,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            TextTrimming = TextTrimming.CharacterEllipsis
        };

        var item = new Border
        {
            Width = ItemWidth,
            Height = 40,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Child = label,
            Cursor = Cursors.Hand
        };

        item.MouseEnter += (_, _) =>
        {
            item.Background = OrangeBrush;
            label.Foreground = Brushes.White;
        };
        item.MouseLeave += (_, _) =>
        {
            item.Background = Brushes.Transparent;
            label.Foreground = normalBrush;
        };
        item.MouseLeftButtonUp += (_, _) =>
        {
            var window = Window.GetWindow(item);
            window?.Close();
            item.Dispatcher.BeginInvoke(new Action(async () =>
            {
                await action();
            }), System.Windows.Threading.DispatcherPriority.ContextIdle);
        };

        return item;
    }
}
