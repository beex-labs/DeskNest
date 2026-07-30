using System.Linq;
using System.Windows;
using System.Windows.Controls;
using BeexWrite.Models;

namespace BeexWrite.Views;

public partial class PreferencesWindow : Window
{
    private readonly AppSettings _settings;

    public PreferencesWindow(AppSettings settings)
    {
        _settings = settings;
        Services.ThemeService.Attach(this);
        InitializeComponent();
        LoadValues();
    }

    private void LoadValues()
    {
        AutoSaveCheck.IsChecked = _settings.AutoSaveEnabled;
        AutoSaveInterval.Text = _settings.AutoSaveIntervalSeconds.ToString();
        SidebarCheck.IsChecked = _settings.SidebarVisible;
        StatusBarCheck.IsChecked = _settings.StatusBarVisible;
        EditorWidthBox.Text = _settings.EditorWidth.ToString();
        SourceModeCheck.IsChecked = _settings.SourceMode;
        FocusModeCheck.IsChecked = _settings.FocusMode;
        TypewriterCheck.IsChecked = _settings.TypewriterMode;

        // Select theme combo
        foreach (ComboBoxItem item in ThemeCombo.Items)
        {
            if (item.Tag as string == _settings.ThemeMode)
            { ThemeCombo.SelectedItem = item; break; }
        }
        // Select language combo
        foreach (ComboBoxItem item in LanguageCombo.Items)
        {
            if (item.Tag as string == _settings.Locale)
            { LanguageCombo.SelectedItem = item; break; }
        }
        // Export settings
        foreach (ComboBoxItem item in PaperSizeCombo.Items)
        {
            if (item.Tag as string == _settings.ExportPaperSize)
            { PaperSizeCombo.SelectedItem = item; break; }
        }
        MarginBox.Text = _settings.ExportMargin;
        BookmarksCheck.IsChecked = _settings.ExportBookmarks;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _settings.AutoSaveEnabled = AutoSaveCheck.IsChecked == true;
        if (int.TryParse(AutoSaveInterval.Text, out var interval) && interval >= 1)
            _settings.AutoSaveIntervalSeconds = interval;
        _settings.SidebarVisible = SidebarCheck.IsChecked == true;
        _settings.StatusBarVisible = StatusBarCheck.IsChecked == true;
        _settings.SourceMode = SourceModeCheck.IsChecked == true;
        _settings.FocusMode = FocusModeCheck.IsChecked == true;
        _settings.TypewriterMode = TypewriterCheck.IsChecked == true;
        if (int.TryParse(EditorWidthBox.Text, out var w) && w >= 400 && w <= 2000)
            _settings.EditorWidth = w;
        if (ThemeCombo.SelectedItem is ComboBoxItem { Tag: string theme })
            _settings.ThemeMode = theme;
        // Export settings
        if (PaperSizeCombo.SelectedItem is ComboBoxItem { Tag: string paper })
            _settings.ExportPaperSize = paper;
        if (!string.IsNullOrWhiteSpace(MarginBox.Text))
            _settings.ExportMargin = MarginBox.Text.Trim();
        _settings.ExportBookmarks = BookmarksCheck.IsChecked == true;
        if (LanguageCombo.SelectedItem is ComboBoxItem { Tag: string locale })
        {
            if (_settings.Locale != locale)
            {
                _settings.Locale = locale;
                // Apply locale change immediately
                var effectiveLocale = locale == "system"
                    ? System.Globalization.CultureInfo.CurrentUICulture.Name
                    : locale;
                Localization.Strings.Instance.LoadLocale(
                    WriteHost.WriteDataDirectory,
                    effectiveLocale);
            }
        }
        DialogResult = true;
    }
}
