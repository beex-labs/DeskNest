using System.Windows;

namespace BeexWrite.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        Services.ThemeService.Attach(this);
        InitializeComponent();
    }
}
