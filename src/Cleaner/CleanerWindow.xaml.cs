using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BeeXCleaner.Models;
using BeeXCleaner.Services;
using BeeXCleaner.ViewModels;
using BeeXCleaner.Views;
using MessageBox = System.Windows.MessageBox;
using CheckBox = System.Windows.Controls.CheckBox;

namespace BeeXCleaner;

public partial class CleanerWindow : Window, IUiService
{
    private readonly MainViewModel _vm;

    public CleanerWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel(this);
        DataContext = _vm;

        Loaded += async (_, _) =>
        {
            LoadDeepLKey();
            await _vm.RefreshAsync();
        };
        InputBindings.Add(new KeyBinding(_vm.RefreshCommand, new KeyGesture(Key.F5)));
    }

    // ---------------- Event Handling ----------------
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Double-clicking the checkbox does not trigger uninstallation
        if (BeeX.DeskNest.VisualTreeUtils.FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null)
            return;
        // Triggered only when a data row is double-clicked
        if (BeeX.DeskNest.VisualTreeUtils.FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is null)
            return;

        if (_vm.UninstallCommand.CanExecute(null))
            _vm.UninstallCommand.Execute(null);
    }

    private void OnHeaderCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        // Check the box to apply the selection to the visible list; uncheck the box to clear the entire list and prevent residual checkmarks from items hidden by search filters.
        _vm.SetAllChecked(cb.IsChecked == true);
    }

    // Right-click to select that row first: This ensures that when “No items selected” is checked, the “Force Delete/Clean Up Residual Data” option will apply to the row you right-clicked on.
    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = BeeX.DeskNest.VisualTreeUtils.FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null)
            row.IsSelected = true;
    }

    // Toolbox drop-down: This process is a system tray application; the MenuItem pop-up in the hierarchical menu does not appear under the global style,
    // Switch to using ContextMenu (the pop-up layer displays correctly) to host the toolbox items; when the button is clicked, the menu opens below it.
    private void OnToolboxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    // ---------------- IUiService Implementation ----------------
    public bool Confirm(string message, string title = "确认")
        => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    public bool ConfirmDanger(string message, string title = "警告")
        => MessageBox.Show(this, message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning)
           == MessageBoxResult.Yes;

    public void Alert(string message, string title = "提示")
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void ShowError(string message, string title = "错误")
        => MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public void CleanResiduals(IReadOnlyList<InstalledProgram> programs)
    {
        if (programs.Count == 0) return;
        var win = new ResidualWindow(programs) { Owner = this };
        win.ShowDialog();
    }

    public void ShowDetails(InstalledProgram program)
    {
        var win = new DetailsWindow(program) { Owner = this };
        win.ShowDialog();
    }

    public void ScanOrphans()
    {
        var win = new OrphanWindow { Owner = this };
        win.ShowDialog();
    }

    public void ShowWipe()
    {
        var win = new WipeWindow { Owner = this };
        win.ShowDialog();
    }

    public void ShowQuickDelete()
    {
        var win = new QuickDeleteWindow { Owner = this };
        win.ShowDialog();
    }

    public void ShowResult(ResidualCleanResult result, string title = "清理完成")
    {
        var win = new ResultWindow(result, title) { Owner = this };
        win.ShowDialog();
    }

    public void ShowBackupRestore()
    {
        var win = new BackupRestoreWindow { Owner = this };
        win.ShowDialog();
    }

    public void ShowCleanupHistory()
    {
        var win = new CleanupHistoryWindow { Owner = this };
        win.ShowDialog();
    }

    // ---------------- DeepL Key Settings ----------------
    private void LoadDeepLKey()
    {
        CleanerDeepLKeyBox.Text = BeeX.DeskNest.UserConfigHelper.ReadDeepLApiKey();
    }

    private void OnDeepLKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        OnDeepLKeyChanged(sender, new RoutedEventArgs());
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void OnDeepLKeyChanged(object sender, RoutedEventArgs e)
    {
        var key = CleanerDeepLKeyBox.Text.Trim();
        BeeX.DeskNest.UserConfigHelper.WriteDeepLApiKey(key);

        // Clear the translation service's key cache so that new keys take effect immediately
        BeeX.DeskNest.TranslateResultWindow.ClearDeepLKeyCache();
    }
}
