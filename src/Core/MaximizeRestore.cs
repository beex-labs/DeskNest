using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Cursors = System.Windows.Input.Cursors;
using Button = System.Windows.Controls.Button;
using Image = System.Windows.Controls.Image;
using Size = System.Windows.Size;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;

namespace BeeX.DeskNest;

/// <summary>
/// 通用「最大化后恢复原窗口大小」按钮：任何窗口被最大化时，在其内容区右上角浮出一个恢复按钮，
/// 点击即还原到最大化前的大小。用 Adorner 注入，无需修改各窗口布局。
/// 仅对自定义无边框（无原生标题栏）的窗口有意义；原生标题栏窗口本身已带还原按钮。
/// </summary>
public static class MaximizeRestore
{
    static readonly Dictionary<Window, RestoreAdorner> active = new();

    public static void Attach(Window window)
    {
        window.StateChanged += (_, _) => Update(window);
        window.Closed += (_, _) => { active.Remove(window); };
    }

    static void Update(Window window)
    {
        if (window.Content is not UIElement content) return;
        var layer = AdornerLayer.GetAdornerLayer(content);
        if (layer == null) return;
        if (window.WindowState == WindowState.Maximized)
        {
            if (!active.ContainsKey(window))
            {
                var adorner = new RestoreAdorner(content, window);
                active[window] = adorner;
                layer.Add(adorner);
            }
        }
        else if (active.TryGetValue(window, out var existing))
        {
            layer.Remove(existing);
            active.Remove(window);
        }
    }

    sealed class RestoreAdorner : Adorner
    {
        readonly VisualCollection visuals;
        readonly Button button;

        public RestoreAdorner(UIElement adorned, Window window) : base(adorned)
        {
            visuals = new VisualCollection(this);
            button = new Button
            {
                Width = 40,
                Height = 32,
                Content = new Image { Source = SvgIcon.Load("restore", 18, Brushes.White), Width = 18, Height = 18, IsHitTestVisible = false },
                Background = new SolidColorBrush(Color.FromArgb(220, 13, 19, 33)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(150, 255, 138, 0)),
                BorderThickness = new Thickness(1),
                Foreground = Brushes.White,
                Cursor = Cursors.Hand,
                ToolTip = Localization.T("恢復視窗大小", Localization.CurrentLanguage)
            };
            button.Click += (_, _) => window.WindowState = WindowState.Normal;
            visuals.Add(button);
        }

        protected override int VisualChildrenCount => visuals.Count;
        protected override Visual GetVisualChild(int index) => visuals[index];
        protected override Size MeasureOverride(Size constraint) { button.Measure(constraint); return button.DesiredSize; }

        protected override Size ArrangeOverride(Size finalSize)
        {
            const double w = 40, h = 32;
            button.Arrange(new Rect(new Point(finalSize.Width - w - 12, 12), new Size(w, h)));
            return finalSize;
        }
    }
}
