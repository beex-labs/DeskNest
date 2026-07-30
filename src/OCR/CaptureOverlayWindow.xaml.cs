using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace BeeX.OCR;

public partial class CaptureOverlayWindow : Window
{
    private readonly Rectangle _screenBounds;
    private readonly double _scaleX;
    private readonly double _scaleY;
    private System.Windows.Point _startPoint;
    private bool _isSelecting;

    public CaptureOverlayWindow(Rectangle screenBounds)
    {
        InitializeComponent();

        _screenBounds = screenBounds;
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
        _scaleX = Math.Max(1.0, graphics.DpiX / 96.0);
        _scaleY = Math.Max(1.0, graphics.DpiY / 96.0);

        Left = screenBounds.Left / _scaleX;
        Top = screenBounds.Top / _scaleY;
        Width = screenBounds.Width / _scaleX;
        Height = screenBounds.Height / _scaleY;

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
    }

    public Rectangle? SelectedScreenBounds { get; private set; }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isSelecting = true;
        _startPoint = e.GetPosition(OverlayCanvas);
        SelectionRectangle.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRectangle, _startPoint.X);
        Canvas.SetTop(SelectionRectangle, _startPoint.Y);
        SelectionRectangle.Width = 0;
        SelectionRectangle.Height = 0;
        CaptureMouse();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        System.Windows.Point current = e.GetPosition(OverlayCanvas);
        double left = Math.Min(_startPoint.X, current.X);
        double top = Math.Min(_startPoint.Y, current.Y);
        double width = Math.Abs(current.X - _startPoint.X);
        double height = Math.Abs(current.Y - _startPoint.Y);

        Canvas.SetLeft(SelectionRectangle, left);
        Canvas.SetTop(SelectionRectangle, top);
        SelectionRectangle.Width = width;
        SelectionRectangle.Height = height;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting)
        {
            return;
        }

        _isSelecting = false;
        ReleaseMouseCapture();

        Rect selectedDip = new(
            Canvas.GetLeft(SelectionRectangle),
            Canvas.GetTop(SelectionRectangle),
            SelectionRectangle.Width,
            SelectionRectangle.Height);

        if (selectedDip.Width < 4 || selectedDip.Height < 4)
        {
            DialogResult = false;
            Close();
            return;
        }

        SelectedScreenBounds = new Rectangle(
            _screenBounds.Left + (int)Math.Round(selectedDip.Left * _scaleX),
            _screenBounds.Top + (int)Math.Round(selectedDip.Top * _scaleY),
            Math.Max(1, (int)Math.Round(selectedDip.Width * _scaleX)),
            Math.Max(1, (int)Math.Round(selectedDip.Height * _scaleY)));

        DialogResult = true;
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            HwndSource source = HwndSource.FromHwnd(hwnd);
            source.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }
}
