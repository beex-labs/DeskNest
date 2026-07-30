using BeeXCleaner.Infrastructure;
using Microsoft.Win32;

namespace BeeXCleaner.Models;

/// <summary>程序来源类型。</summary>
public enum ProgramSource
{
    /// <summary>传统 Win32 程序（来自注册表 Uninstall 项）。</summary>
    Win32,
    /// <summary>UWP / Microsoft Store 应用。</summary>
    Uwp
}

/// <summary>
/// 表示一个已安装的程序条目。
/// </summary>
public sealed class InstalledProgram : ObservableObject
{
    // -------- 基本信息 --------
    public string DisplayName { get; init; } = string.Empty;
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public string? UrlInfoAbout { get; init; }

    private DateTime? _installDate;
    /// <summary>安装日期。注册表未提供时由后台按安装目录创建时间填充。</summary>
    public DateTime? InstallDate
    {
        get => _installDate;
        set
        {
            if (SetProperty(ref _installDate, value))
                OnPropertyChanged(nameof(InstallDateDisplay));
        }
    }

    private long _sizeBytes;
    /// <summary>估算大小（字节）。注册表 EstimatedSize 缺失时由后台按安装目录实测填充。</summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    /// <summary>大小是否为实测值（非注册表 EstimatedSize）。</summary>
    public bool SizeMeasured { get; set; }

    // -------- 定位信息（用于强制删除 / 详情） --------
    public ProgramSource Source { get; init; } = ProgramSource.Win32;

    /// <summary>注册表根（HKLM / HKCU）。</summary>
    public RegistryHive Hive { get; init; } = RegistryHive.LocalMachine;

    /// <summary>注册表视图（64 位 / 32 位 WOW6432Node）。</summary>
    public RegistryView View { get; init; } = RegistryView.Registry64;

    /// <summary>Uninstall 下的子项相对路径。</summary>
    public string RegistrySubKeyPath { get; init; } = string.Empty;

    /// <summary>注册表项名称（可能是 MSI 的 ProductCode GUID）。</summary>
    public string RegistryKeyName { get; init; } = string.Empty;

    /// <summary>若为 MSI 产品，则为其 ProductCode（形如 {GUID}）。</summary>
    public string? MsiProductCode { get; init; }

    /// <summary>UWP 应用的 PackageFullName。</summary>
    public string? PackageFullName { get; init; }

    // -------- UI 状态 --------
    private bool _isSelected;
    /// <summary>批量卸载勾选状态。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // -------- 显示辅助 --------
    public string HiveDisplay => Source == ProgramSource.Uwp
        ? "Store"
        : Hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";

    public string ArchDisplay => Source == ProgramSource.Uwp
        ? "UWP"
        : View == RegistryView.Registry32 ? "32-bit" : "64-bit";

    public string VersionDisplay => string.IsNullOrWhiteSpace(DisplayVersion) ? "—" : DisplayVersion!;
    public string PublisherDisplay => string.IsNullOrWhiteSpace(Publisher) ? "—" : Publisher!;
    public string SizeDisplay => SizeBytes <= 0
        ? "—"
        : FormatSize(SizeBytes) + (SizeMeasured ? "（实测）" : "");
    public string InstallDateDisplay => InstallDate?.ToString("yyyy-MM-dd") ?? "—";

    /// <summary>
    /// 完整注册表路径（物理路径，用于详情展示、regedit 定位与 reg.exe 备份）。
    /// 32 位程序（Registry32 视图）的真实键位于 WOW6432Node 下，这里补全前缀，
    /// 否则 64 位的 reg.exe/regedit 会定位/导出到不存在（或错误）的 64 位同名键。
    /// </summary>
    public string FullRegistryPath
    {
        get
        {
            if (Source == ProgramSource.Uwp) return $"(UWP) {PackageFullName}";
            var root = Hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
            var sub = RegistrySubKeyPath;
            if (Hive == RegistryHive.LocalMachine && View == RegistryView.Registry32
                && sub.StartsWith(@"SOFTWARE\", StringComparison.OrdinalIgnoreCase)
                && !sub.Contains("WOW6432Node", StringComparison.OrdinalIgnoreCase))
                sub = @"SOFTWARE\WOW6432Node" + sub["SOFTWARE".Length..];
            return $@"{root}\{sub}\{RegistryKeyName}";
        }
    }

    public bool CanNormalUninstall =>
        Source == ProgramSource.Uwp
        || !string.IsNullOrWhiteSpace(UninstallString)
        || !string.IsNullOrWhiteSpace(QuietUninstallString);

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return u == 0 ? $"{size:0} {units[u]}" : $"{size:0.0} {units[u]}";
    }
}
