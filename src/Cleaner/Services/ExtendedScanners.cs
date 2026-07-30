using System.IO;
using System.Xml.Linq;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// 扩展遗留扫描（第 8 章）：计划任务 / 服务 / PATH / 防火墙 / 文件关联。
/// 统一判据：被引用的“本机本地可执行文件/目录确实不存在”才视为遗留。
/// 这些类型删除风险高于普通残留，默认全部不勾选（仅展示与可选删除），交用户确认。
/// </summary>
public static class ExtendedScanner
{
    /// <summary>聚合运行各扩展扫描器。includeFileAssociations 默认 false（风险最高，仅深度扫描开启）。</summary>
    public static List<ResidualItem> ScanOrphans(bool includeFileAssociations = false)
    {
        var all = new List<ResidualItem>();
        Collect(all, "计划任务", ScheduledTaskScanner.ScanOrphans);
        Collect(all, "服务", ServiceScanner.ScanOrphans);
        Collect(all, "PATH", PathScanner.ScanOrphans);
        Collect(all, "防火墙", FirewallScanner.ScanOrphans);
        if (includeFileAssociations)
            Collect(all, "文件关联", FileAssociationScanner.ScanOrphans);
        return all;
    }

    private static void Collect(List<ResidualItem> all, string label, Func<List<ResidualItem>> scan)
    {
        try { all.AddRange(scan()); }
        catch (Exception ex) { AppLogger.Warn($"{label}扫描失败", ex); }
    }

    /// <summary>
    /// 判断被引用路径是否为“本机本地磁盘上、但已不存在”的可执行文件/目录。
    /// 展开环境变量、处理 \??\ 与 \SystemRoot\ 前缀；网络/NAS 与非绝对路径不判定。
    /// </summary>
    internal static bool IsMissingLocalTarget(string? raw, out string resolved)
    {
        resolved = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var exe = raw!.Trim().Trim('"');
        if (exe.StartsWith(@"\??\", StringComparison.Ordinal)) exe = exe[4..];
        var windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (exe.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            exe = windir + exe[11..];
        else if (exe.StartsWith("system32\\", StringComparison.OrdinalIgnoreCase))
            exe = Path.Combine(windir, exe);

        try { exe = Environment.ExpandEnvironmentVariables(exe).Trim().Trim('"'); }
        catch { return false; }
        if (exe.Length == 0 || !Path.IsPathRooted(exe)) return false;
        if (FileSystemUtil.IsNetworkPath(exe)) return false;

        resolved = exe;
        try { return !File.Exists(exe) && !Directory.Exists(exe); }
        catch { return false; }
    }
}

/// <summary>计划任务扫描：读取 Tasks 目录内任务 XML，动作可执行文件不存在则为遗留（8.1）。</summary>
internal static class ScheduledTaskScanner
{
    public static List<ResidualItem> ScanOrphans()
    {
        var results = new List<ResidualItem>();
        var tasksRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
        if (!Directory.Exists(tasksRoot)) return results;

        List<string> files;
        try { files = Directory.EnumerateFiles(tasksRoot, "*", SearchOption.AllDirectories).ToList(); }
        catch { return results; }

        foreach (var file in files)
        {
            try
            {
                var doc = XDocument.Load(file);
                // 用 LocalName 匹配，规避任务 XML 命名空间
                var commands = doc.Descendants()
                    .Where(e => e.Name.LocalName == "Command")
                    .Select(e => e.Value.Trim())
                    .Where(v => v.Length > 0)
                    .ToList();
                if (commands.Count == 0) continue;

                var rel = file[tasksRoot.Length..].Replace('/', '\\').TrimStart('\\');
                var taskName = "\\" + rel;

                foreach (var cmd in commands)
                {
                    if (!ExtendedScanner.IsMissingLocalTarget(cmd, out var resolved)) continue;
                    results.Add(new ResidualItem
                    {
                        Type = ResidualType.ScheduledTask,
                        Path = taskName,
                        Payload = taskName,
                        MatchReason = $"计划任务目标已不存在：{resolved}",
                        Confidence = ResidualConfidence.High,
                        Risk = ResidualRisk.Caution,
                        Source = ResidualSource.ScheduledTask,
                        CanAutoSelect = false
                    });
                    break;
                }
            }
            catch { /* 单个任务解析失败忽略 */ }
        }
        return results;
    }
}

/// <summary>服务扫描：ImagePath 指向的 .exe 不存在则为遗留（8.2）。删除风险高，默认不勾选。</summary>
internal static class ServiceScanner
{
    public static List<ResidualItem> ScanOrphans()
    {
        var results = new List<ResidualItem>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var services = baseKey.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return results;

        foreach (var name in services.GetSubKeyNames())
        {
            try
            {
                using var svc = services.OpenSubKey(name);
                if (svc?.GetValue("ImagePath") is not string imagePath || string.IsNullOrWhiteSpace(imagePath))
                    continue;

                var exe = ExtractServiceExe(imagePath);
                if (exe is null) continue;                     // 只处理明确的 .exe 服务镜像
                if (!ExtendedScanner.IsMissingLocalTarget(exe, out var resolved)) continue;

                var display = svc.GetValue("DisplayName") as string ?? name;
                results.Add(new ResidualItem
                {
                    Type = ResidualType.Service,
                    Path = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}",
                    Payload = name,
                    MatchReason = $"服务「{display}」镜像已不存在：{resolved}",
                    Confidence = ResidualConfidence.High,
                    Risk = ResidualRisk.Dangerous,
                    Source = ResidualSource.Service,
                    CanAutoSelect = false
                });
            }
            catch { /* 单个服务忽略 */ }
        }
        return results;
    }

    /// <summary>从 ImagePath 提取可执行文件路径；仅返回以 .exe 结尾者，其余（svchost/驱动等）返回 null。</summary>
    private static string? ExtractServiceExe(string imagePath)
    {
        var s = imagePath.Trim();
        string exe;
        if (s.StartsWith('"'))
        {
            var end = s.IndexOf('"', 1);
            exe = end > 1 ? s[1..end] : s.Trim('"');
        }
        else
        {
            var idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = idx > 0 ? s[..(idx + 4)] : s.Split(' ')[0];
        }
        return exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe : null;
    }
}

/// <summary>PATH 扫描：用户/系统 PATH 中指向不存在目录的条目（8.3）。默认不勾选。</summary>
internal static class PathScanner
{
    public static List<ResidualItem> ScanOrphans()
    {
        var results = new List<ResidualItem>();
        AddScope(results, EnvironmentVariableTarget.User, "User");
        AddScope(results, EnvironmentVariableTarget.Machine, "Machine");
        return results;
    }

    private static void AddScope(List<ResidualItem> results, EnvironmentVariableTarget scope, string label)
    {
        string? value;
        try { value = Environment.GetEnvironmentVariable("PATH", scope); }
        catch { return; }
        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (var raw in value.Split(';'))
        {
            var dir = raw.Trim();
            if (dir.Length == 0) continue;

            string expanded;
            try { expanded = Environment.ExpandEnvironmentVariables(dir).Trim().Trim('"'); }
            catch { continue; }
            if (!Path.IsPathRooted(expanded)) continue;
            if (FileSystemUtil.IsNetworkPath(expanded)) continue;

            bool missing;
            try { missing = !Directory.Exists(expanded); }
            catch { continue; }
            if (!missing) continue;

            results.Add(new ResidualItem
            {
                Type = ResidualType.PathEntry,
                Path = dir,
                Payload = label,
                MatchReason = $"{label} PATH 中的目录已不存在",
                Confidence = ResidualConfidence.Medium,
                Risk = ResidualRisk.Caution,
                Source = ResidualSource.Path,
                CanAutoSelect = false
            });
        }
    }
}

/// <summary>防火墙规则扫描：注册表规则的关联程序 App= 不存在则为遗留（8.4）。默认不勾选。</summary>
internal static class FirewallScanner
{
    private const string RulesKey =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules";

    public static List<ResidualItem> ScanOrphans()
    {
        var results = new List<ResidualItem>();
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(RulesKey);
        if (key is null) return results;

        // netsh 按名称删除会删光全部同名规则（不同程序常用相同显示名）：
        // 先按名称聚合，只有“同名规则全部失效”才列为遗留，避免波及仍有效的规则。
        // 无 App= 的同名规则视为有效（无法判定其失效）。
        var missingByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // 名称 → 任一失效路径
        var validNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var valueName in key.GetValueNames())
        {
            try
            {
                if (key.GetValue(valueName) is not string rule || string.IsNullOrWhiteSpace(rule)) continue;
                var display = GetField(rule, "Name=") ?? valueName;
                var app = GetField(rule, "App=");
                if (app is null || !ExtendedScanner.IsMissingLocalTarget(app, out var resolved))
                {
                    validNames.Add(display);
                    continue;
                }
                missingByName.TryAdd(display, resolved);
            }
            catch { /* 单条规则忽略 */ }
        }

        foreach (var (display, resolved) in missingByName)
        {
            if (validNames.Contains(display)) continue; // 同名中尚有有效规则，按名删除会误伤
            results.Add(new ResidualItem
            {
                Type = ResidualType.FirewallRule,
                Path = display,
                Payload = display,
                MatchReason = $"防火墙规则关联程序已不存在：{resolved}",
                Confidence = ResidualConfidence.Medium,
                Risk = ResidualRisk.Caution,
                Source = ResidualSource.Firewall,
                CanAutoSelect = false
            });
        }
        return results;
    }

    private static string? GetField(string rule, string prefix)
    {
        foreach (var part in rule.Split('|'))
            if (part.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return part[prefix.Length..];
        return null;
    }
}

/// <summary>
/// 文件关联扫描（8.5，风险最高）：HKCU/HKLM Software\Classes\Applications 下
/// shell\open\command 指向已删除程序的关联项。默认不勾选，仅深度扫描启用。
/// </summary>
internal static class FileAssociationScanner
{
    public static List<ResidualItem> ScanOrphans()
    {
        var results = new List<ResidualItem>();
        ScanApplications(results, RegistryHive.CurrentUser);
        ScanApplications(results, RegistryHive.LocalMachine);
        return results;
    }

    private static void ScanApplications(List<ResidualItem> results, RegistryHive hive)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var apps = baseKey.OpenSubKey(@"SOFTWARE\Classes\Applications");
            if (apps is null) return;

            var rootName = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
            foreach (var appName in apps.GetSubKeyNames())
            {
                if (!appName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    using var cmdKey = apps.OpenSubKey($@"{appName}\shell\open\command");
                    if (cmdKey?.GetValue(null) is not string cmd || string.IsNullOrWhiteSpace(cmd)) continue;

                    var (exe, _) = UninstallService.ParseCommandLine(Environment.ExpandEnvironmentVariables(cmd));
                    if (!ExtendedScanner.IsMissingLocalTarget(exe, out var resolved)) continue;

                    results.Add(new ResidualItem
                    {
                        Type = ResidualType.RegistryKey,
                        Path = $@"{rootName}\SOFTWARE\Classes\Applications\{appName}",
                        MatchReason = $"文件关联「{appName}」指向已删除程序：{resolved}",
                        Confidence = ResidualConfidence.Low,
                        Risk = ResidualRisk.Dangerous,
                        Source = ResidualSource.FileAssociation,
                        CanAutoSelect = false
                    });
                }
                catch { /* 单项忽略 */ }
            }
        }
        catch { /* 无权限或不存在 */ }
    }
}
