using System.Windows;
using System.Windows.Media;

namespace BeeX.DeskNest;

/// <summary>WPF 可视化树常用辅助方法。</summary>
internal static class VisualTreeUtils
{
    /// <summary>沿可视化树向上查找指定类型的父元素，未找到返回 null。</summary>
    public static T? FindParent<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
