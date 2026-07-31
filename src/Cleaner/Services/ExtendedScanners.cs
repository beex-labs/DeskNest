using System.IO;
using System.Xml.Linq;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// Extended Legacy Scan (Chapter 8): Scheduled Tasks / Services / PATH / Firewall / File Associations.
/// Uniform Criteria: A case is considered a legacy issue only if the cited "local executable file/directory on this machine does not exist."
/// These types carry a higher risk of deletion than ordinary residual data; by default, none of them are checked (only displayed as optional for deletion), and the user must confirm.
/// </summary>
public static class ExtendedScanner
{
    /// <summary> Runs all extension scanners in aggregate. includeFileAssociations is set to false by default (highest risk; enabled only for deep scans). </summary>
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
    /// Determine whether the referenced path is an executable file or directory that is "located on a local disk on this machine but no longer exists."
    /// Expand environment variables; handle the \??\ and \SystemRoot\ prefixes; do not check for network/NAS or non-absolute paths.
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

/// <summary>Scheduled Task Scan: Reads the task XML files in the Tasks directory; if the executable file for an action does not exist, it is marked as legacy (8.1). </summary>
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
                // Use LocalName for matching to work around the task XML namespace
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
            catch { /* Ignore if a single task fails to parse */ }
        }
        return results;
    }
}

/// <summary>Service scan: If the .exe file pointed to by ImagePath does not exist, it is considered legacy (8.2). Deletion carries a high risk; this option is unchecked by default. </summary>
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
                if (exe is null) continue;                     // Process only explicit .exe service images
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
            catch { /* Ignore a Single Service */ }
        }
        return results;
    }

    /// <summary>Extracts the executable file path from ImagePath; returns only those ending in .exe; returns null for all others (such as svchost or drivers). </summary>
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

/// <summary>PATH Scan: Entries in the user/system PATH that point to nonexistent directories (8.3). Unchecked by default. </summary>
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

/// <summary>Firewall Rule Scan: If the associated program for a registry rule (App=) does not exist, it is considered legacy (8.4). This option is unchecked by default. </summary>
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

        // Using `netsh` to delete by name will remove all rules with that name (different programs often use the same display name):
        // First, aggregate by name; only rules where “all rules with the same name are invalid” are listed as legacy, to avoid affecting rules that are still valid.
        // Rules with the same name but without "App=" are considered valid (it is not possible to determine that they are invalid).
        var missingByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); // Name → Any Failure Path
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
            catch { /* Ignore a single rule */ }
        }

        foreach (var (display, resolved) in missingByName)
        {
            if (validNames.Contains(display)) continue; // There are valid rules for names with the same name, so deleting by name may result in unintended consequences.
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
/// File Association Scan (8.5, Highest Risk): Under HKCU/HKLM\Software\Classes\Applications
/// "shell\open\command" points to an association entry for a deleted program. This option is unchecked by default and is enabled only during a deep scan.
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
                catch { /* Single-Item Exclusion */ }
            }
        }
        catch { /* No permission or does not exist */ }
    }
}
