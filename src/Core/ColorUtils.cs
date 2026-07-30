using System.Windows.Media;

namespace BeeX.DeskNest;

/// <summary>颜色格式转换辅助方法。</summary>
internal static class ColorUtils
{
    /// <summary>将各种格式的颜色字符串规范化为 #RRGGBB，失败返回 fallback。</summary>
    public static string NormalizeHexColor(string value, string fallback)
    {
        value = (value ?? "").Trim();
        if (!value.StartsWith("#")) value = "#" + value;
        try
        {
            var color = (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(value);
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }
        catch { return fallback; }
    }

    /// <summary>RGB 转 HSV（H: 0-360, S: 0-1, V: 0-1）。</summary>
    public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rf = r / 255.0, gf = g / 255.0, bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf)), min = Math.Min(rf, Math.Min(gf, bf)), delta = max - min;
        v = max;
        s = max <= 0 ? 0 : delta / max;
        if (delta <= 0) { h = 0; return; }
        if (max == rf) h = 60 * ((((gf - bf) / delta) % 6 + 6) % 6);
        else if (max == gf) h = 60 * (((bf - rf) / delta) + 2);
        else h = 60 * (((rf - gf) / delta) + 4);
        if (h < 0) h += 360;
    }

    /// <summary>HSV 转 WPF Color（H: 0-360, S: 0-1, V: 0-1）。</summary>
    public static System.Windows.Media.Color HsvToColor(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s, x = c * (1 - Math.Abs((h / 60) % 2 - 1)), m = v - c;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return System.Windows.Media.Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
