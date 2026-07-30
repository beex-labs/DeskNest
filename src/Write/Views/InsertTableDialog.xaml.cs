using System.Windows;

namespace BeexWrite.Views;

public partial class InsertTableDialog : Window
{
    public int Rows { get; private set; } = 3;
    public int Columns { get; private set; } = 3;

    public InsertTableDialog()
    {
        Services.ThemeService.Attach(this);
        InitializeComponent();
    }

    private void OnInsert(object sender, RoutedEventArgs e)
    {
        Rows = ParseClamped(RowsBox.Text, 1, 100, 3);
        Columns = ParseClamped(ColsBox.Text, 1, 30, 3);
        DialogResult = true;
    }

    private static int ParseClamped(string text, int min, int max, int fallback)
    {
        if (!int.TryParse(text?.Trim(), out var value)) value = fallback;
        return value < min ? min : value > max ? max : value;
    }
}
