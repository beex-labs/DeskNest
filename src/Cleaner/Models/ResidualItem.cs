using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Models;

/// <summary>残留项类型。</summary>
public enum ResidualType
{
    /// <summary>文件夹。</summary>
    Folder,
    /// <summary>文件。</summary>
    File,
    /// <summary>注册表项。</summary>
    RegistryKey,
    /// <summary>快捷方式。</summary>
    Shortcut,
    /// <summary>Windows 服务。</summary>
    Service,
    /// <summary>计划任务。</summary>
    ScheduledTask,
    /// <summary>PATH 环境变量条目（某个目录）。</summary>
    PathEntry,
    /// <summary>防火墙规则。</summary>
    FirewallRule
}

/// <summary>匹配置信度：高（默认可勾选）/ 中 / 低（默认不勾选）。</summary>
public enum ResidualConfidence { High, Medium, Low }

/// <summary>风险等级：普通 / 需确认 / 高风险。</summary>
public enum ResidualRisk { Normal, Caution, Dangerous }

/// <summary>残留来源（匹配依据），供 UI 说明命中原因。</summary>
public enum ResidualSource
{
    InstallDir, Registry, Shortcut, Orphan, DeepScan,
    ScheduledTask, Service, Path, Firewall, FileAssociation, Manual
}

/// <summary>
/// 表示一条卸载后残留记录（文件/文件夹/注册表/快捷方式）。
/// </summary>
public sealed class ResidualItem : ObservableObject
{
    public ResidualType Type { get; init; }

    /// <summary>完整路径（文件系统路径或注册表完整路径）。</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>
    /// 注册表值名。仅当需要删除“某个注册表值”而非整个键时设置（如遗留的 Run 自启动项）。
    /// 为 null 时对 RegistryKey 类型执行整键删除。
    /// </summary>
    public string? RegistryValueName { get; init; }

    /// <summary>大小（字节），注册表项通常为 0。</summary>
    public long SizeBytes { get; init; }

    /// <summary>匹配原因（命中的关键词），用于让用户判断是否为误报。</summary>
    public string MatchReason { get; init; } = string.Empty;

    /// <summary>匹配置信度（High=证据充分；Low=仅名称近似）。</summary>
    public ResidualConfidence Confidence { get; init; } = ResidualConfidence.High;

    /// <summary>风险等级（Dangerous 项即使命中也默认不勾选）。</summary>
    public ResidualRisk Risk { get; init; } = ResidualRisk.Normal;

    /// <summary>来源（匹配依据），供 UI 说明命中方式。</summary>
    public ResidualSource Source { get; init; } = ResidualSource.Registry;

    /// <summary>是否可默认自动勾选（低置信 / 高风险项为 false，交用户确认）。</summary>
    public bool CanAutoSelect { get; init; } = true;

    /// <summary>类型相关附加数据（服务名 / 任务路径 / PATH 作用域 / 防火墙规则名等）。</summary>
    public string? Payload { get; init; }

    private bool? _isSelected;
    /// <summary>是否勾选删除（未显式设置时由 <see cref="CanAutoSelect"/> 决定）。</summary>
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
