using System.Windows.Media;

namespace BeeX.DeskNest;

/// <summary>標題欄高度規範：無視 DPI 縮放，螢幕物理測量恆為 65px（65 ÷ 縮放比例 = WPF 邏輯值）。</summary>
public static class TitleBarMetrics
{
    /// <summary>標題欄物理像素高度基準。</summary>
    public const double PhysicalHeight = 65;

    /// <summary>把 65 物理像素換算成 visual 所在顯示器下的 WPF 邏輯單位；visual 不可用時退回 100% 縮放。</summary>
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
