using System.Windows;

namespace BeexWrite.Views;

public partial class PromptDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public PromptDialog(string title, string prompt, string initial = "")
    {
        Services.ThemeService.Attach(this);
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        ValueBox.Text = initial;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length > 0) DialogResult = true;
    }
}
