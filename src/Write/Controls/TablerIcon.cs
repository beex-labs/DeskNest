using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Xml.Linq;
using Application = System.Windows.Application;
using Point = System.Windows.Point;

namespace BeexWrite.Controls;

/// <summary>
/// Renders a vendored Tabler icon (24x24, 2px stroke) as a WPF <see cref="Shape"/>
/// so its stroke follows the current theme brush. Set <see cref="Icon"/> to the
/// icon file name without extension, e.g. Icon="menu-2".
/// </summary>
public sealed class TablerIcon : Shape
{
    private Geometry _geometry = Geometry.Empty;

    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon), typeof(string), typeof(TablerIcon),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender, OnIconChanged));

    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    public TablerIcon()
    {
        Stretch = Stretch.Uniform;
        StrokeThickness = 2;
        StrokeStartLineCap = PenLineCap.Round;
        StrokeEndLineCap = PenLineCap.Round;
        StrokeLineJoin = PenLineJoin.Round;
        Width = 18;
        Height = 18;
    }

    protected override Geometry DefiningGeometry => _geometry;

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((TablerIcon)d).LoadIcon((string)e.NewValue);
    }

    private void LoadIcon(string icon)
    {
        _geometry = string.IsNullOrWhiteSpace(icon) ? Geometry.Empty : Build(icon);
        InvalidateVisual();
    }

    private static Geometry Build(string icon)
    {
        try
        {
            var uri = new Uri($"/Write/Assets/icons/tabler/{icon}.svg", UriKind.Relative);
            var info = Application.GetResourceStream(uri);
            if (info is null) return Geometry.Empty;

            using var stream = info.Stream;
            var doc = XDocument.Load(stream);
            var group = new GeometryGroup { FillRule = FillRule.Nonzero };

            foreach (var el in doc.Descendants())
            {
                var g = ElementToGeometry(el);
                if (g != null) group.Children.Add(g);
            }
            return group.Children.Count > 0 ? group : Geometry.Empty;
        }
        catch
        {
            return Geometry.Empty;
        }
    }

    private static Geometry? ElementToGeometry(XElement el)
    {
        switch (el.Name.LocalName)
        {
            case "path":
                var d = (string?)el.Attribute("d");
                return string.IsNullOrWhiteSpace(d) ? null : SafeParse(d);
            case "circle":
                return new EllipseGeometry(
                    new Point(D(el, "cx"), D(el, "cy")), D(el, "r"), D(el, "r"));
            case "ellipse":
                return new EllipseGeometry(
                    new Point(D(el, "cx"), D(el, "cy")), D(el, "rx"), D(el, "ry"));
            case "rect":
                var rect = new RectangleGeometry(
                    new Rect(D(el, "x"), D(el, "y"), D(el, "width"), D(el, "height")));
                var rx = D(el, "rx");
                if (rx > 0) { rect.RadiusX = rx; rect.RadiusY = D(el, "ry", rx); }
                return rect;
            case "line":
                return new LineGeometry(
                    new Point(D(el, "x1"), D(el, "y1")), new Point(D(el, "x2"), D(el, "y2")));
            case "polyline":
            case "polygon":
                return PointsToGeometry((string?)el.Attribute("points"), el.Name.LocalName == "polygon");
            default:
                return null;
        }
    }

    private static Geometry? SafeParse(string data)
    {
        try { return Geometry.Parse(data); }
        catch { return null; }
    }

    private static Geometry? PointsToGeometry(string? points, bool close)
    {
        if (string.IsNullOrWhiteSpace(points)) return null;
        var tokens = points.Split(new[] { ' ', ',', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length < 4) return null;

        var sb = new System.Text.StringBuilder();
        for (var i = 0; i + 1 < tokens.Length; i += 2)
        {
            sb.Append(i == 0 ? 'M' : 'L').Append(tokens[i]).Append(' ').Append(tokens[i + 1]).Append(' ');
        }
        if (close) sb.Append('Z');
        return SafeParse(sb.ToString());
    }

    private static double D(XElement el, string name, double fallback = 0)
    {
        var v = (string?)el.Attribute(name);
        return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ? result : fallback;
    }
}
