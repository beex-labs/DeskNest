using System.Windows;
using System.Windows.Media;

namespace BeeX.DeskNest;

/// <summary>Common WPF visual-tree helpers.</summary>
internal static class VisualTreeUtils
{
    /// <summary>Walks up the visual tree to find a parent of the given type; returns null if not found.</summary>
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
