using BeeXCleaner.Infrastructure;
using Microsoft.Win32;

namespace BeeXCleaner.Models;

/// <summary>Program source type. </summary>
public enum ProgramSource
{
    /// <summary>Traditional Win32 programs (from the Uninstall registry key).</summary>
    Win32,
    /// <summary>UWP / Microsoft Store app.</summary>
    Uwp
}

/// <summary>
/// Indicates an entry for an installed program.
/// </summary>
public sealed class InstalledProgram : ObservableObject
{
    // -------- Basic Information --------
    public string DisplayName { get; init; } = string.Empty;
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }
    public string? DisplayIcon { get; init; }
    public string? UrlInfoAbout { get; init; }

    private DateTime? _installDate;
    /// <summary>Installation date. If not provided in the registry, the system automatically fills in the value based on the creation date of the installation directory. </summary>
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
    /// <summary>Estimated size (bytes). If the "EstimatedSize" registry entry is missing, the background process will populate it based on actual measurements taken in the installation directory. </summary>
    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            if (SetProperty(ref _sizeBytes, value))
                OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    /// <summary>Is the size the actual measured value (not the "EstimatedSize" in the registry)? </summary>
    public bool SizeMeasured { get; set; }

    // -------- Location Information (for forced deletion / details) --------
    public ProgramSource Source { get; init; } = ProgramSource.Win32;

    /// <summary>Registry root (HKLM / HKCU).</summary>
    public RegistryHive Hive { get; init; } = RegistryHive.LocalMachine;

    /// <summary>Registry View (64-bit / 32-bit WOW6432Node). </summary>
    public RegistryView View { get; init; } = RegistryView.Registry64;

    /// <summary>Relative path to the subitems under "Uninstall." </summary>
    public string RegistrySubKeyPath { get; init; } = string.Empty;

    /// <summary>Registry key name (possibly the MSI's ProductCode GUID).</summary>
    public string RegistryKeyName { get; init; } = string.Empty;

    /// <summary>For MSI products, this is the ProductCode (in the form of {GUID}).</summary>
    public string? MsiProductCode { get; init; }

    /// <summary>The PackageFullName of the UWP app.</summary>
    public string? PackageFullName { get; init; }

    // -------- UI Status --------
    private bool _isSelected;
    /// <summary>Bulk uncheck selection status.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // -------- Display Help --------
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
    /// Full registry path (physical path, used for displaying details, locating the registry with regedit, and backing up with reg.exe).
    /// The actual key for a 32-bit program (Registry32 view) is located under WOW6432Node; here, the prefix is added,
    /// Otherwise, the 64-bit version of reg.exe/regedit will navigate to or export a 64-bit key with the same name that does not exist (or is invalid).
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
