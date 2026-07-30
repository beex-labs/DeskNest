using System.Windows.Media;
using System.Windows.Media.Imaging;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;

namespace BeeX.DeskNest;

internal static class ContrastHelper
{
    static readonly SolidColorBrush DarkText = Frozen(Color.FromRgb(13, 19, 33));
    static readonly SolidColorBrush LightText = Frozen(Colors.White);

    public static Brush TextFor(Brush? background, Brush? fallback = null)
    {
        if (TryColor(background, out var color)) return RelativeLuminance(color) > .48 ? DarkText : LightText;
        return fallback ?? DarkText;
    }

    public static Brush TextFor(BitmapSource? image, Brush? fallback = null)
    {
        if (image == null) return fallback ?? DarkText;
        try
        {
            var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
            var width = converted.PixelWidth;
            var height = converted.PixelHeight;
            if (width == 0 || height == 0) return fallback ?? DarkText;
            const int samples = 12;
            double red = 0, green = 0, blue = 0, weight = 0;
            var pixel = new byte[4];
            for (var y = 0; y < samples; y++)
            for (var x = 0; x < samples; x++)
            {
                var px = Math.Min(width - 1, (int)((x + .5) * width / samples));
                var py = Math.Min(height - 1, (int)((y + .5) * height / samples));
                converted.CopyPixels(new System.Windows.Int32Rect(px, py, 1, 1), pixel, 4, 0);
                var alpha = pixel[3] / 255d;
                red += pixel[2] * alpha; green += pixel[1] * alpha; blue += pixel[0] * alpha; weight += alpha;
            }
            if (weight <= .01) return fallback ?? DarkText;
            return RelativeLuminance(Color.FromRgb((byte)(red / weight), (byte)(green / weight), (byte)(blue / weight))) > .48 ? DarkText : LightText;
        }
        catch { return fallback ?? DarkText; }
    }

    public static bool TryColor(Brush? brush, out Color color)
    {
        if (brush is SolidColorBrush solid)
        {
            color = solid.Color;
            return color.A > 24;
        }
        if (brush is ImageBrush { ImageSource: BitmapSource image })
        {
            color = TextFor(image) == DarkText ? Colors.White : Color.FromRgb(13, 19, 33);
            return true;
        }
        color = default;
        return false;
    }

    static double RelativeLuminance(Color c)
    {
        static double Channel(byte value)
        {
            var x = value / 255d;
            return x <= .04045 ? x / 12.92 : Math.Pow((x + .055) / 1.055, 2.4);
        }
        return .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
    }

    static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
