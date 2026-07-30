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

    // ---------------- 事件处理 ----------------
    private void OnGridDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 双击复选框时不触发卸载
        if (BeeX.DeskNest.VisualTreeUtils.FindParent<CheckBox>(e.OriginalSource as DependencyObject) is not null)
            return;
        // 仅在双击到数据行时触发
        if (BeeX.DeskNest.VisualTreeUtils.FindParent<DataGridRow>(e.OriginalSource as DependencyObject) is null)
            return;

        if (_vm.UninstallCommand.CanExecute(null))
            _vm.UninstallCommand.Execute(null);
    }

    private void OnHeaderCheckClick(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        // 勾选作用于可见列表；取消勾选清空全量，避免被搜索过滤隐藏的勾选项残留
        _vm.SetAllChecked(cb.IsChecked == true);
    }

    // 右键先选中该行：确保“未勾选任何项”时，强制删除/清理残留能作用于右键的这一行
    private void OnGridRightClick(object sender, MouseButtonEventArgs e)
    {
        var row = BeeX.DeskNest.VisualTreeUtils.FindParent<DataGridRow>(e.OriginalSource as DependencyObject);
        if (row is not null)
            row.IsSelected = true;
    }

    // 工具箱下拉：本进程为托盘应用，层级菜单的 MenuItem 弹层在全局样式下不显示，
    // 改用 ContextMenu（弹层可正常显示）承载工具箱项，点击按钮时在其下方打开。
    private void OnToolboxClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.PlacementTarget = btn;
        btn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        btn.ContextMenu.IsOpen = true;
    }

    // ---------------- IUiService 实现 ----------------
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

    // ---------------- DeepL Key 设置 ----------------
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

        // 清除翻译服务的 Key 缓存，使新 Key 立即生效
        BeeX.DeskNest.TranslateResultWindow.ClearDeepLKeyCache();
    }
}
