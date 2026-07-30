using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace BeeX.OCR;

internal static class BeeXDialog
{
    public static void ShowMessage(Window owner, string titleText, string headingText, string messageText)
    {
        Window dialog = CreateDialog(owner, titleText, 420, 220);
        Grid shell = CreateShell(dialog, owner, titleText);

        var body = new Grid { Margin = new Thickness(34, 22, 34, 24) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        if (!string.Equals(titleText, headingText, StringComparison.Ordinal))
        {
            content.Children.Add(new TextBlock
            {
                Text = headingText,
                FontSize = 17,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 10)
            });
        }

        content.Children.Add(new TextBlock
        {
            Text = messageText,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(content);

        var ok = new Button
        {
            Content = "确定",
            Width = 72,
            Background = new SolidColorBrush(Color.FromRgb(255, 138, 0)),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)owner.FindResource(typeof(Button))
        };
        ok.Click += (_, _) => dialog.Close();
        Grid.SetRow(ok, 1);
        body.Children.Add(ok);

        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        dialog.Content = CreateDialogBorder(shell);
        dialog.ShowDialog();
    }

    public static bool ShowConfirm(
        Window owner,
        string titleText,
        string headingText,
        string messageText,
        string primaryText,
        string secondaryText)
    {
        bool accepted = false;
        Window dialog = CreateDialog(owner, titleText, 440, 230);
        Grid shell = CreateShell(dialog, owner, titleText);

        var body = new Grid { Margin = new Thickness(34, 22, 34, 24) };
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        content.Children.Add(new TextBlock
        {
            Text = headingText,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 10)
        });
        content.Children.Add(new TextBlock
        {
            Text = messageText,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            LineHeight = 21,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(content);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var secondary = new Button
        {
            Content = secondaryText,
            Width = 72,
            Style = (Style)owner.FindResource(typeof(Button))
        };
        secondary.Click += (_, _) => dialog.Close();
        var primary = new Button
        {
            Content = primaryText,
            Width = 72,
            Background = new SolidColorBrush(Color.FromRgb(255, 138, 0)),
            Foreground = Brushes.White,
            Style = (Style)owner.FindResource(typeof(Button))
        };
        primary.Click += (_, _) =>
        {
            accepted = true;
            dialog.Close();
        };
        buttons.Children.Add(secondary);
        buttons.Children.Add(primary);
        Grid.SetRow(buttons, 1);
        body.Children.Add(buttons);

        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        dialog.Content = CreateDialogBorder(shell);
        dialog.ShowDialog();
        return accepted;
    }

    public static void ShowAbout(Window owner)
    {
        Window dialog = CreateDialog(owner, "关于", 560, 300);
        Grid shell = CreateShell(dialog, owner, "BeeX_OCR");

        var body = new Grid { Margin = new Thickness(34, 18, 34, 22) };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        body.ColumnDefinitions.Add(new ColumnDefinition());
        body.RowDefinitions.Add(new RowDefinition());
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var about = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        about.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),
            Width = 88,
            Height = 88,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 16)
        });
        about.Children.Add(new TextBlock
        {
            Text = "Flow Faster. Work Smarter.",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        body.Children.Add(about);

        var description = new StackPanel
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 30, 0, 0)
        };
        description.Children.Add(new TextBlock
        {
            Text = "说明",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 14)
        });
        description.Children.Add(new TextBlock
        {
            Text = "轻量 OCR 文字提取工具。\n框选屏幕、图片文件或剪贴板图片后识别文字。\n识别完成后会自动复制到剪贴板，也可一键翻译结果。",
            Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128)),
            LineHeight = 22,
            TextWrapping = TextWrapping.Wrap
        });
        Grid.SetColumn(description, 2);
        body.Children.Add(description);

        var ok = new Button
        {
            Content = "确定",
            Width = 72,
            Background = new SolidColorBrush(Color.FromRgb(255, 138, 0)),
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)owner.FindResource(typeof(Button))
        };
        ok.Click += (_, _) => dialog.Close();
        Grid.SetRow(ok, 1);
        Grid.SetColumn(ok, 2);
        body.Children.Add(ok);

        Grid.SetRow(body, 1);
        shell.Children.Add(body);

        dialog.Content = CreateDialogBorder(shell);
        dialog.ShowDialog();
    }

    private static Window CreateDialog(Window owner, string titleText, double width, double height)
    {
        var dialog = new Window
        {
            Owner = owner,
            Title = titleText,
            Width = width,
            Height = height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = false,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            ShowInTaskbar = false,
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            Foreground = owner.Foreground
        };

        WindowChrome.SetWindowChrome(
            dialog,
            new WindowChrome
            {
                ResizeBorderThickness = new Thickness(0),
                CaptionHeight = 0,
                CornerRadius = new CornerRadius(14),
                GlassFrameThickness = new Thickness(0),
                UseAeroCaptionButtons = false
            });

        dialog.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 1 &&
                e.GetPosition(dialog).Y <= 42 &&
                !IsInteractive(e.OriginalSource as DependencyObject) &&
                e.LeftButton == MouseButtonState.Pressed)
            {
                e.Handled = true;
                dialog.DragMove();
            }
        };

        return dialog;
    }

    private static Grid CreateShell(Window dialog, Window owner, string titleText)
    {
        var shell = new Grid();
        // 標題欄物理 65px：無視 DPI 縮放，螢幕實測恆為 65px
        var titleHeight = 65d;
        try { var scale = System.Windows.Media.VisualTreeHelper.GetDpi(owner ?? dialog).DpiScaleY; if (scale > 0) titleHeight = 65 / scale; } catch { }
        shell.RowDefinitions.Add(new RowDefinition { Height = new GridLength(titleHeight) });
        shell.RowDefinitions.Add(new RowDefinition());

        var title = new Grid { Margin = new Thickness(14, 0, 8, 0) };
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/BeeX.png")),
            Width = 20,
            Height = 20
        });
        brand.Children.Add(new TextBlock
        {
            Text = titleText,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.SemiBold
        });
        title.Children.Add(brand);

        var close = new Button
        {
            Content = "X",
            FontSize = 15,
            Style = (Style)owner.FindResource("CaptionButtonStyle")
        };
        close.Click += (_, _) => dialog.Close();
        Grid.SetColumn(close, 1);
        title.Children.Add(close);
        shell.Children.Add(title);

        return shell;
    }

    private static Border CreateDialogBorder(UIElement child)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(245, 247, 250)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(183, 192, 204)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            ClipToBounds = true,
            Child = child
        };
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
