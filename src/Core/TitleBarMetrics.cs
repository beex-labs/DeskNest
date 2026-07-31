using System.Windows.Media;

namespace BeeX.DeskNest;

/// <summary>Title bar height standard: ignores DPI scaling; the physical screen measurement is always 65px (65 / scale factor = WPF logical value).</summary>
public static class TitleBarMetrics
{
    /// <summary>Physical-pixel height baseline of the title bar.</summary>
    public const double PhysicalHeight = 65;

    /// <summary>Converts 65 physical pixels to WPF logical units under the monitor the visual is on; falls back to 100% scaling when the visual is unavailable.</summary>
    public static double Dip(Visual? visual)
    {
        try
        {
            var target = visual ?? System.Windows.Application.Current?.MainWindow;
            if (target != null)
            {
                var scale = VisualTreeHelper.GetDpi(target).DpiScaleY;
                if (scale > 0) return PhysicalHeight / scale;
            }
        }
        catch { }
        return PhysicalHeight;
    }
}
