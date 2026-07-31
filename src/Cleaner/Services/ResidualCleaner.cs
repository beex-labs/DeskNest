using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// Cleanup results. PendingReboot: Number of items that are in use/protected and will be deleted after a scheduled reboot.
/// In addition to the count, retain the list of deletions, failures, and restarts, as well as warnings and backup paths, for display in the structured results window and logs (6.2).
/// </summary>
public sealed class ResidualCleanResult
{
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int PendingReboot { get; set; }
    public long FreedBytes { get; set; }
    public string Log { get; set; } = string.Empty;

    /// <summary> Registry backup directory (this value is present only if a backup was created during this cleanup). </summary>
    public string? BackupPath { get; set; }

    /// <summary>Path to the session log file (if saved to disk). </summary>
    public string? LogPath { get; set; }

    public List<string> DeletedItems { get; } = new();
    public List<string> FailedItems { get; } = new();
    public List<string> PendingRebootItems { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// A unified forced cleanup mechanism that can be reused for both residual cleanup and legacy cleanup.
/// First remove the read-only and hidden system attributes from files/directories before deleting them; applies only to local disks on this machine; hard-blocking of the root directory in case of disaster.
/// Items that cannot be deleted (because they are in use or protected) will be scheduled for deletion after the system restarts. You can delete entire keys or individual registry values.
/// Before deleting the registry, services, or PATH, a backup is automatically created in the session directory (6.1).
/// </summary>
public static class ResidualCleaner
{
    /// <summary>
    /// Perform cleanup (delete the items selected by the user).
    /// <paramref name="session"/> When not empty: Automatically back up the registry, services, and PATH before deletion, and include the log in the session.
    /// <paramref name="killProcesses"/> Default: false—Closes handles only; if deletion fails, the process is deleted after a restart (processes are not silently terminated; see 6.4).
    /// </summary>
    public static ResidualCleanResult Clean(IEnumerable<ResidualItem> items, bool secureErase = false,
        CleanupSession? session = null, bool killProcesses = false)
    {
        var result = new ResidualCleanResult();
        var log = new StringBuilder();

        foreach (var item in items.Where(i => i.IsSelected))
        {
            try
            {
                switch (item.Type)
                {
                    case ResidualType.Folder:
                        CleanFolder(item, secureErase, killProcesses, result, log);
                        break;
                    case ResidualType.File:
                    case ResidualType.Shortcut:
                        CleanFile(item, secureErase, killProcesses, result, log);
                        break;
                    case ResidualType.RegistryKey:
                        CleanRegistry(item, session, result, log);
                        break;
                    case ResidualType.Service:
                        CleanService(item, session, result, log);
                        break;
                    case ResidualType.ScheduledTask:
                        CleanScheduledTask(item, result, log);
                        break;
                    case ResidualType.PathEntry:
                        CleanPathEntry(item, session, result, log);
                        break;
                    case ResidualType.FirewallRule:
                        CleanFirewallRule(item, result, log);
                        break;
                }
            }
            catch (Exception ex)
            {
                result.Failed++;
                result.FailedItems.Add(item.Path);
                log.AppendLine($"✗ 删除失败 {item.Path}: {ex.Message}");
                AppLogger.Warn($"清理失败: {item.Path}", ex);
            }
        }

        result.Log = log.ToString().Trim();

        if (session is not null)
        {
            result.BackupPath = session.HasBackups ? session.BackupFolder : null;
            if (result.Log.Length > 0) session.Log(result.Log);
        }
        return result;
    }

    // ---------------- File System ----------------

    private static void CleanFolder(ResidualItem item, bool secureErase, bool killProcesses,
        ResidualCleanResult r, StringBuilder log)
    {
        if (!UninstallService.IsSafeToDelete(item.Path))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 已保护未删（{ProtectReason(item.Path)}）: {item.Path}");
            return;
        }
        switch (FileSystemUtil.ForceDeleteDirectory(item.Path, secureErase, killProcesses))
        {
            case DeleteResult.Removed:
                r.FreedBytes += item.SizeBytes; r.Deleted++; r.DeletedItems.Add(item.Path);
                log.AppendLine($"✔ 删除文件夹: {item.Path}");
                break;
            case DeleteResult.ScheduledReboot:
                r.PendingReboot++; r.PendingRebootItems.Add(item.Path);
                log.AppendLine($"↻ 部分被占用/受保护，已安排重启后删除: {item.Path}"
                    + (secureErase ? "（该部分未能安全擦除，重启删除后数据仍可能被恢复）" : ""));
                if (secureErase)
                    r.Warnings.Add($"安排重启后删除的内容未经安全擦除: {item.Path}");
                break;
            default:
                r.Failed++; r.FailedItems.Add(item.Path);
                log.AppendLine($"✗ 删除失败: {item.Path}");
                break;
        }
    }

    private static void CleanFile(ResidualItem item, bool secureErase, bool killProcesses,
        ResidualCleanResult r, StringBuilder log)
    {
        if (FileSystemUtil.IsNetworkPath(item.Path))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 已保护未删（网络/NAS 路径）: {item.Path}");
            return;
        }
        // File-Level System Protection (6.5): Files in Windows/System32/SysWOW64 are protected against deletion
        if (item.Type == ResidualType.File && !UninstallService.IsSafeFileToDelete(item.Path))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 已保护未删（系统关键目录文件）: {item.Path}");
            return;
        }
        switch (FileSystemUtil.ForceDeleteFile(item.Path, secureErase, killProcesses))
        {
            case DeleteResult.Removed:
                r.FreedBytes += item.SizeBytes; r.Deleted++; r.DeletedItems.Add(item.Path);
                log.AppendLine($"✔ 删除{item.TypeDisplay}: {item.Path}");
                break;
            case DeleteResult.ScheduledReboot:
                r.PendingReboot++; r.PendingRebootItems.Add(item.Path);
                log.AppendLine($"↻ 被占用，已安排重启后删除: {item.Path}"
                    + (secureErase ? "（未能安全擦除，重启删除后数据仍可能被恢复）" : ""));
                if (secureErase)
                    r.Warnings.Add($"安排重启后删除的文件未经安全擦除: {item.Path}");
                break;
            default:
                r.Failed++; r.FailedItems.Add(item.Path);
                log.AppendLine($"✗ 删除失败: {item.Path}");
                break;
        }
    }

    // ---------------- Registry (Automatically backs up before deletion, 6.1) -----------------

    private static void CleanRegistry(ResidualItem item, CleanupSession? session,
        ResidualCleanResult r, StringBuilder log)
    {
        // Deep Defense: Protected root-level keys are never completely deleted (this does not occur during normal scans; the defense mechanism protects against manually crafted attacks and data anomalies).
        if (item.RegistryValueName is null && ResidualScanner.IsProtectedRegistryRoot(item.Path))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 已保护未删（系统/软件根级注册表键）: {item.Path}");
            return;
        }

        if (session is not null)
        {
            var backup = RegistryBackup.Export(item.Path, session.EnsureBackupFolder());
            if (backup is not null)
                log.AppendLine($"💾 已备份注册表: {System.IO.Path.GetFileName(backup)}");
            else if (RegistryKeyExists(item.Path))
            {
                // The key still exists, but the backup failed: Deletion aborted. Without a backup, there is no way to recover the data; we cannot let the promise of “automatic backup before deletion” go unfulfilled.
                r.Failed++; r.FailedItems.Add(item.Path);
                r.Warnings.Add($"注册表备份失败，已中止删除（无备份即无法恢复）: {item.Path}");
                log.AppendLine($"⛔ 注册表备份失败，已中止删除: {item.Path}");
                return;
            }
            // The key no longer exists (e.g., if it was deleted earlier by the uninstaller): No backup is needed; proceed with deletion (which is effectively a no-op) to keep the count consistent.
        }

        if (item.RegistryValueName is not null)
        {
            DeleteRegistryValue(item.Path, item.RegistryValueName);
            log.AppendLine($"✔ 删除注册表值: {item.Path} → {item.RegistryValueName}");
        }
        else
        {
            DeleteRegistryKey(item.Path);
            log.AppendLine($"✔ 删除注册表项: {item.Path}");
        }
        r.Deleted++; r.DeletedItems.Add(item.Path);
    }

    // ---------------- Services (sc.exe; back up the service registry keys before deleting) -----------------

    private static void CleanService(ResidualItem item, CleanupSession? session,
        ResidualCleanResult r, StringBuilder log)
    {
        var name = item.Payload;
        if (string.IsNullOrWhiteSpace(name))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ 服务名缺失，未删除: {item.Path}");
            return;
        }
        if (HasUnsafeQuote(name!))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 服务名含引号，存在命令行注入风险，已拒绝: {name}");
            return;
        }

        if (session is not null)
        {
            // Same contract as CleanRegistry: Abort deletion if the backup fails and the service registry key still exists,
            // Otherwise, the promise of “automatic backup before deletion” would be unfulfilled, resulting in irrecoverable deletions without a backup.
            var svcKey = $@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\{name}";
            var backup = RegistryBackup.Export(svcKey, session.EnsureBackupFolder());
            if (backup is null && RegistryKeyExists(svcKey))
            {
                r.Failed++; r.FailedItems.Add(item.Path);
                r.Warnings.Add($"服务注册表备份失败，已中止删除（无备份即无法恢复）: {name}");
                log.AppendLine($"⛔ 服务注册表备份失败，已中止删除: {name}");
                return;
            }
        }

        RunSystemCommand("sc.exe", $"stop \"{name}\"", ignoreFailure: true);
        var (ok, output) = RunSystemCommand("sc.exe", $"delete \"{name}\"");
        if (ok)
        {
            r.Deleted++; r.DeletedItems.Add(item.Path);
            log.AppendLine($"✔ 删除服务: {name}");
        }
        else
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ 删除服务失败: {name} {output}");
        }
    }

    // ---------------- Scheduled Tasks (schtasks.exe) -----------------

    private static void CleanScheduledTask(ResidualItem item, ResidualCleanResult r, StringBuilder log)
    {
        var taskName = string.IsNullOrWhiteSpace(item.Payload) ? item.Path : item.Payload!;
        if (HasUnsafeQuote(taskName))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 任务名含引号，存在命令行注入风险，已拒绝: {taskName}");
            return;
        }
        var (ok, output) = RunSystemCommand("schtasks.exe", $"/delete /tn \"{taskName}\" /f");
        if (ok)
        {
            r.Deleted++; r.DeletedItems.Add(item.Path);
            log.AppendLine($"✔ 删除计划任务: {taskName}");
        }
        else
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ 删除计划任务失败: {taskName} {output}");
        }
    }

    // ---------------- PATH Entries (Back up original values before deleting) -----------------

    private static void CleanPathEntry(ResidualItem item, CleanupSession? session,
        ResidualCleanResult r, StringBuilder log)
    {
        // Payload: Scope "User"/"Machine"; Path: Directory to be removed (retains the original %VAR% from the scan).
        // Manually edit the registry and use `DoNotExpandEnvironmentNames` to read the value: If using `Environment.Get/SetEnvironmentVariable`,
        // The entire PATH will be expanded, and the value type will change from REG_EXPAND_SZ to REG_SZ, permanently breaking any %VAR% references it contains.
        var machine = string.Equals(item.Payload, "Machine", StringComparison.OrdinalIgnoreCase);
        var scopeName = machine ? "Machine" : "User";
        var subKeyPath = machine
            ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
            : "Environment";

        using var baseKey = RegistryKey.OpenBaseKey(
            machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(subKeyPath, writable: true);
        var raw = key?.GetValue("Path", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (key is null || string.IsNullOrEmpty(raw))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ 无法读取 {scopeName} PATH: {item.Path}");
            return;
        }

        RegistryValueKind kind;
        try { kind = key.GetValueKind("Path"); }
        catch { kind = RegistryValueKind.ExpandString; }

        // Matches both the "original text" and "expanded" forms simultaneously, and is compatible with invalid entries containing %VAR%.
        var target = NormalizePathEntry(item.Path);
        var targetExpanded = NormalizePathEntry(SafeExpand(item.Path));
        var parts = raw!.Split(';');
        var kept = parts.Where(p =>
        {
            var n = NormalizePathEntry(p);
            if (n.Length == 0) return true; // Leave blank lines as is; do not modify irrelevant content
            return !n.Equals(target, StringComparison.OrdinalIgnoreCase)
                   && !NormalizePathEntry(SafeExpand(p)).Equals(targetExpanded, StringComparison.OrdinalIgnoreCase);
        }).ToArray();

        if (kept.Length == parts.Length)
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ {scopeName} PATH 中未找到该目录: {item.Path}");
            return;
        }

        if (session is not null && !BackupPathValue(session.EnsureBackupFolder(), scopeName, raw!, log))
        {
            // A successful backup is a prerequisite for overwriting the PATH: without a backup, it is impossible to restore entries that were accidentally deleted.
            r.Failed++; r.FailedItems.Add(item.Path);
            r.Warnings.Add($"PATH 备份失败，已中止移除（无备份即无法恢复）: {item.Path}");
            log.AppendLine($"⛔ PATH 备份失败，已中止移除: {item.Path}");
            return;
        }

        // Keep the original value type (default: REG_EXPAND_SZ) and broadcast notifications of environment variable changes
        key.SetValue("Path", string.Join(";", kept),
            kind == RegistryValueKind.String ? RegistryValueKind.String : RegistryValueKind.ExpandString);
        BroadcastEnvironmentChange();
        r.Deleted++; r.DeletedItems.Add(item.Path);
        log.AppendLine($"✔ 从 {scopeName} PATH 移除: {item.Path}");
    }

    private static string NormalizePathEntry(string p) => p.Trim().Trim('"').TrimEnd('\\');

    private static string SafeExpand(string p)
    {
        try { return Environment.ExpandEnvironmentVariables(p); }
        catch { return p; }
    }

    private static bool BackupPathValue(string backupFolder, string scopeName,
        string value, StringBuilder log)
    {
        try
        {
            Directory.CreateDirectory(backupFolder);
            // When deleting multiple items in the same session, back them up one by one: If you use a fixed filename, subsequent backups will overwrite the earlier ones, resulting in the loss of the first backup containing the complete original values.
            var file = EnsureUniqueFile(System.IO.Path.Combine(backupFolder, $"PATH-{scopeName}.txt"));
            File.WriteAllText(file, value, Encoding.UTF8);
            log.AppendLine($"💾 已备份 {scopeName} PATH 原值: {System.IO.Path.GetFileName(file)}");
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("备份 PATH 失败", ex);
            return false;
        }
    }

    /// <summary>If the target file already exists, append a sequence number to avoid overwriting existing backups.</summary>
    private static string EnsureUniqueFile(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;
        var dir = System.IO.Path.GetDirectoryName(filePath)!;
        var stem = System.IO.Path.GetFileNameWithoutExtension(filePath);
        var ext = System.IO.Path.GetExtension(filePath);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = System.IO.Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return filePath;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam,
        string lParam, int fuFlags, int uTimeout, out IntPtr lpdwResult);

    private const int HWND_BROADCAST = 0xffff;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SMTO_ABORTIFHUNG = 0x0002;

    /// <summary>Broadcast WM_SETTINGCHANGE("Environment") to notify Explorer and new processes of changes to the PATH. </summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                "Environment", SMTO_ABORTIFHUNG, 3000, out _);
        }
        catch { /* This is for informational purposes only; a failure does not affect the deletion results. */ }
    }

    // ---------------- Firewall Rules (netsh.exe) -----------------

    private static void CleanFirewallRule(ResidualItem item, ResidualCleanResult r, StringBuilder log)
    {
        var ruleName = string.IsNullOrWhiteSpace(item.Payload) ? item.Path : item.Payload!;
        // Rule named "any string": Embedded quotes can be used to construct parameter escapes such as "name=all," which would delete all rules at once; this must be prevented.
        if (HasUnsafeQuote(ruleName))
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"⛔ 规则名含引号，存在命令行注入风险，已拒绝: {ruleName}");
            return;
        }
        var (ok, output) = RunSystemCommand("netsh.exe",
            $"advfirewall firewall delete rule name=\"{ruleName}\"");
        if (ok)
        {
            r.Deleted++; r.DeletedItems.Add(item.Path);
            log.AppendLine($"✔ 删除防火墙规则: {ruleName}");
        }
        else
        {
            r.Failed++; r.FailedItems.Add(item.Path);
            log.AppendLine($"✗ 删除防火墙规则失败: {ruleName} {output}");
        }
    }

    // ---------------- System Command Execution ----------------

    /// <summary>Does the name contain double quotes? (If concatenated into the command line, this will cause parameter boundaries to be escaped, expanding the scope of the deletion.) </summary>
    private static bool HasUnsafeQuote(string name) => name.Contains('"');

    /// <summary> Executes system commands (sc/schtasks/netsh) and returns a success/failure status along with a summary of the output. In case of failure, it writes to the internal log. </summary>
    private static (bool ok, string output) RunSystemCommand(string fileName, string arguments,
        bool ignoreFailure = false)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return (false, "无法启动进程");

            // Read from two output streams asynchronously to avoid the "subprocess hangs + synchronous ReadToEnd blocks indefinitely" scenario, which renders the 30-second timeout meaningless.
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"{fileName} {arguments} 执行超时(30s)，已强制结束");
                return (false, "命令执行超时");
            }
            proc.WaitForExit(); // Ensure that the asynchronous output buffer is completely flushed
            var stdout = outTask.GetAwaiter().GetResult();
            var stderr = errTask.GetAwaiter().GetResult();

            var ok = proc.ExitCode == 0;
            if (!ok && !ignoreFailure)
                AppLogger.Warn($"{fileName} {arguments} 退出码 {proc.ExitCode}: {stderr.Trim()}");
            var msg = stderr.Trim().Length > 0 ? stderr.Trim() : stdout.Trim();
            return (ok, msg);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"执行 {fileName} 失败", ex);
            return (false, ex.Message);
        }
    }

    private static string ProtectReason(string path)
        => FileSystemUtil.IsNetworkPath(path) ? "网络/NAS 路径，仅限本机" : "操作系统关键根目录";

    // ---------------- Registry Deletion ----------------

    /// <summary>Check whether a registry key exists (to distinguish between "backup failed" and "the key never existed"). </summary>
    internal static bool RegistryKeyExists(string fullPath)
    {
        try
        {
            var (hive, rel) = SplitRegistryPath(fullPath);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(rel);
            return key is not null;
        }
        catch { return true; } // When uncertainty exists, assume it is present and take the safe approach of "abort if the backup fails."
    }

    public static void DeleteRegistryKey(string fullPath)
    {
        var (hive, rel) = SplitRegistryPath(fullPath);
        var idx = rel.LastIndexOf('\\');
        if (idx <= 0) throw new InvalidOperationException("无效的注册表路径。");
        var parent = rel[..idx];
        var name = rel[(idx + 1)..];

        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var parentKey = baseKey.OpenSubKey(parent, writable: true);
        parentKey?.DeleteSubKeyTree(name, throwOnMissingSubKey: false);
    }

    public static void DeleteRegistryValue(string fullKeyPath, string valueName)
    {
        var (hive, rel) = SplitRegistryPath(fullKeyPath);
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(rel, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
    }

    public static (RegistryHive hive, string relative) SplitRegistryPath(string fullPath)
    {
        const string hkcu = @"HKEY_CURRENT_USER\";
        const string hklm = @"HKEY_LOCAL_MACHINE\";
        if (fullPath.StartsWith(hkcu, StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.CurrentUser, fullPath[hkcu.Length..]);
        if (fullPath.StartsWith(hklm, StringComparison.OrdinalIgnoreCase))
            return (RegistryHive.LocalMachine, fullPath[hklm.Length..]);
        throw new InvalidOperationException("不支持的注册表根。");
    }
}

