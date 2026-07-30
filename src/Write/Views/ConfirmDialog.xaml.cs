using System.Windows;

namespace BeexWrite.Views;

/// <summary>Themed confirm dialog. Result: Confirm / Third (optional) / Cancel.</summary>
public partial class ConfirmDialog : Window
{
    public enum ConfirmResult { Cancel, Confirm, Third }

    public ConfirmResult Result { get; private set; } = ConfirmResult.Cancel;

    public ConfirmDialog(string message, string confirmText, string cancelText, string? thirdText = null)
    {
        Services.ThemeService.Attach(this);
        InitializeComponent();
        MessageText.Text = message;
        BtnConfirm.Content = confirmText;
        BtnCancel.Content = cancelText;
        if (!string.IsNullOrEmpty(thirdText))
        {
            BtnThird.Content = thirdText;
            BtnThird.Visibility = Visibility.Visible;
        }
        else
        {
            Width = 380; // two-button layout needs less room
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e) { Result = ConfirmResult.Confirm; DialogResult = true; }
    private void OnThird(object sender, RoutedEventArgs e) { Result = ConfirmResult.Third; DialogResult = true; }
    private void OnCancel(object sender, RoutedEventArgs e) { Result = ConfirmResult.Cancel; DialogResult = false; }
}
