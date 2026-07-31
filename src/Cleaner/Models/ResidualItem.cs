using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Models;

/// <summary>Residual item type. </summary>
public enum ResidualType
{
    /// The <summary> folder. </summary>
    Folder,
    /// <summary> file. </summary>
    File,
    /// <summary> Registry key. </summary>
    RegistryKey,
    /// <summary>Shortcut. </summary>
    Shortcut,
    /// <summary>Windows Service. </summary>
    Service,
    /// <summary>Scheduled Tasks. </summary>
    ScheduledTask,
    /// <summary>PATH environment variable entry (a directory).</summary>
    PathEntry,
    /// <summary>Firewall rules. </summary>
    FirewallRule
}

/// <summary>Fit reliability: High (checked by default) / Medium / Low (unchecked by default).</summary>
public enum ResidualConfidence { High, Medium, Low }

/// <summary>Risk Level: Normal / To Be Confirmed / High Risk. </summary>
public enum ResidualRisk { Normal, Caution, Dangerous }

/// <summary>Residual source (match criteria), used by the UI to explain the reason for the match. </summary>
public enum ResidualSource
{
    InstallDir, Registry, Shortcut, Orphan, DeepScan,
    ScheduledTask, Service, Path, Firewall, FileAssociation, Manual
}

/// <summary>
/// Indicates a residual entry (file/folder/registry entry/shortcut) left behind after uninstallation.
/// </summary>
public sealed class ResidualItem : ObservableObject
{
    public ResidualType Type { get; init; }

    /// <summary>Full path (file system path or full registry path). </summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// Registry value name. Set this only when you need to delete "a specific registry value" rather than the entire key (such as a leftover "Run" startup entry).
    /// If the value is null, the entire RegistryKey is deleted.
    /// </summary>
    public string? RegistryValueName { get; init; }

    /// <summary>Size (in bytes); the registry entry is typically 0. </summary>
    public long SizeBytes { get; init; }

    /// <summary>Reason for match (keywords that triggered the match), used to help users determine whether it is a false positive. </summary>
    public string MatchReason { get; init; } = string.Empty;

    /// <summary>Match Confidence (High = Strong Evidence; Low = Only Similar Names). </summary>
    public ResidualConfidence Confidence { get; init; } = ResidualConfidence.High;

    /// <summary>Risk Level (The "Dangerous" option is unchecked by default, even if it matches). </summary>
    public ResidualRisk Risk { get; init; } = ResidualRisk.Normal;

    /// <summary>Source (matching criteria), used to describe how the UI is matched. </summary>
    public ResidualSource Source { get; init; } = ResidualSource.Registry;

    /// <summary>Should this be automatically checked by default (set to "false" for low-confidence/high-risk items, requiring user confirmation)? </summary>
    public bool CanAutoSelect { get; init; } = true;

    /// <summary>Additional data related to the type (service name / task path / PATH scope / firewall rule name, etc.).</summary>
    public string? Payload { get; init; }

    private bool? _isSelected;
    /// <summary>Whether to check the "Delete" box (determined by <see cref="CanAutoSelect"/> if not explicitly set). </summary>
    public bool IsSelected
    {
        get => _isSelected ?? CanAutoSelect;
        set
        {
            if ((_isSelected ?? CanAutoSelect) == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public string TypeDisplay => Type switch
    {
        ResidualType.Folder => "文件夹",
        ResidualType.File => "文件",
        ResidualType.RegistryKey => "注册表",
        ResidualType.Shortcut => "快捷方式",
        ResidualType.Service => "服务",
        ResidualType.ScheduledTask => "计划任务",
        ResidualType.PathEntry => "PATH 条目",
        ResidualType.FirewallRule => "防火墙规则",
        _ => "未知"
    };

    public string ConfidenceDisplay => Confidence switch
    {
        ResidualConfidence.High => "高",
        ResidualConfidence.Medium => "中",
        ResidualConfidence.Low => "低",
        _ => ""
    };

    public string RiskDisplay => Risk switch
    {
        ResidualRisk.Normal => "普通",
        ResidualRisk.Caution => "需确认",
        ResidualRisk.Dangerous => "高风险",
        _ => ""
    };

    public string SizeDisplay => Type is ResidualType.Folder or ResidualType.File or ResidualType.Shortcut
        ? InstalledProgram.FormatSize(SizeBytes)
        : "—";
}
