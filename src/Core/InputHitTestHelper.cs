using System.Windows;
using System.Windows.Media;

namespace BeeX.DeskNest;

internal static class InputHitTestHelper
{
    public static bool IsInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is System.Windows.Controls.Primitives.ButtonBase
                or System.Windows.Controls.TextBox
                or System.Windows.Controls.PasswordBox
                or System.Windows.Controls.ComboBox
                or System.Windows.Controls.Slider
                or System.Windows.Controls.Primitives.ScrollBar
                or System.Windows.Controls.Primitives.Thumb
                or System.Windows.Controls.ListBox
                or System.Windows.Controls.ListBoxItem
                or System.Windows.Controls.MenuItem
                or System.Windows.Controls.CheckBox
                or System.Windows.Controls.RadioButton)
                return true;

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }
}
