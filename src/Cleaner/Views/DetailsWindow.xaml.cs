using System.Diagnostics;
using System.IO;
using System.Windows;
using BeeXCleaner.Models;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace BeeXCleaner.Views;

public partial class DetailsWindow : Window
{
    private readonly InstalledProgram _program;

    public DetailsWindow(InstalledProgram program)
    {
        InitializeComponent();
        _program = program;
        DataContext = program;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var loc = _program.InstallLocation;
        if (string.IsNullOrWhiteSpace(loc) || !Directory.Exists(loc))
        {
            MessageBox.Show(this, "未找到安装目录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryStart(new ProcessStartInfo("explorer.exe", $"\"{loc}\"") { UseShellExecute = true });
    }

    private void OnOpenRegistry(object sender, RoutedEventArgs e)
    {
        if (_program.Source == ProgramSource.Uwp)
        {
            MessageBox.Show(this, "UWP 应用没有传统注册表卸载项。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var path = _program.FullRegistryPath;
        try
        {
            // Enter the LastKey in regedit so that it automatically navigates to that location when opened (Windows 10 and later; the "Computer\" prefix is accepted in all languages)
            using var key = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
            key?.SetValue("LastKey", $@"Computer\{path}");

            Clipboard.SetText(path);
            TryStart(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });

            MessageBox.Show(this,
                "已打开注册表编辑器。\n若未自动定位，注册表路径已复制到剪贴板，可粘贴到 regedit 顶部地址栏后回车。",
                "定位注册表", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnOpenWebsite(object sender, RoutedEventArgs e)
    {
        var url = _program.UrlInfoAbout;
        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(this, "该程序未提供官网地址。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        TryStart(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void TryStart(ProcessStartInfo psi)
    {
        try { Process.Start(psi); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
