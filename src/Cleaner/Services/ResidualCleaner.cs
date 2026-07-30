using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// 清理结果。PendingReboot：被占用/受保护、已安排重启后删除的项数。
/// 除计数外保留删除/失败/重启清单、警告与备份路径，供结构化结果窗口与日志展示（6.2）。
/// </summary>
public sealed class ResidualCleanResult
{
    public int Deleted { get; set; }
    public int Failed { get; set; }
    public int PendingReboot { get; set; }
    public long FreedBytes { get; set; }
    public string Log { get; set; } = string.Empty;

    /// <summary>注册表备份目录（本次清理产生了备份时才有值）。</summary>
    public string? BackupPath { get; set; }

    /// <summary>会话日志文件路径（若已落盘）。</summary>
    public string? LogPath { get; set; }

    public List<string> DeletedItems { get; } = new();
    public List<string> FailedItems { get; } = new();
    public List<string> PendingRebootItems { get; } = new();
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 统一的强制清理器，供残留清理与遗留清理复用。
/// 文件/目录先清除只读隐藏系统属性再删；仅限本机本地磁盘；灾难性根目录硬拦截。
/// 删不掉的（被占用/受保护）安排在系统重启后删除。支持删除整键或单个注册表值。
/// 删除注册表/服务/PATH 前会自动备份到会话目录（6.1）。
/// </summary>
public static class ResidualCleaner
{
    /// <summary>
    /// 执行清理（删除用户勾选的项）。
    /// <paramref name="session"/> 非空时：删注册表/服务/PATH 前自动备份，并把日志并入会话。
    /// <paramref name="killProcesses"/> 默认 false：仅关句柄，删不掉则安排重启后删（不静默杀进程，见 6.4）。
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

    // ---------------- 文件系统 ----------------

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
        // 文件级系统保护（6.5）：位于 Windows/System32/SysWOW64 的文件禁止删除
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

    // ---------------- 注册表（删前自动备份，6.1）----------------

    private static void CleanRegistry(ResidualItem item, CleanupSession? session,
        ResidualCleanResult r, StringBuilder log)
    {
        // 深度防御：受保护的根级键永不整删（正常扫描不会产生，防御手工构造/数据异常）
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
                // 键仍存在但备份失败：中止删除。无备份即无恢复途径，不能让“删前自动备份”的承诺落空。
                r.Failed++; r.FailedItems.Add(item.Path);
                r.Warnings.Add($"注册表备份失败，已中止删除（无备份即无法恢复）: {item.Path}");
                log.AppendLine($"⛔ 注册表备份失败，已中止删除: {item.Path}");
                return;
            }
            // 键已不存在（如被卸载器提前删掉）：无需备份，继续走删除（实际为 no-op）保持计数一致。
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

    // ---------------- 服务（sc.exe，删前备份服务注册表键）----------------

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
            // 与 CleanRegistry 同一契约：备份失败且服务注册表键仍存在时中止删除，
            // 否则“删前自动备份”的承诺落空，产生无备份的不可恢复删除。
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

    // ---------------- 计划任务（schtasks.exe）----------------

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

    // ---------------- PATH 条目（删前备份原值）----------------

    private static void CleanPathEntry(ResidualItem item, CleanupSession? session,
        ResidualCleanResult r, StringBuilder log)
    {
        // Payload：作用域 "User"/"Machine"；Path：要移除的目录（保留扫描时的 %VAR% 原文）。
        // 直接操作注册表并用 DoNotExpandEnvironmentNames 读取：若经 Environment.Get/SetEnvironmentVariable，
        // 整条 PATH 会被展开且值类型从 REG_EXPAND_SZ 漂移为 REG_SZ，永久破坏其中的 %VAR% 引用。
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

        // 同时按“原始原文”与“展开后”两种形态匹配，兼容含 %VAR% 的失效条目
        var target = NormalizePathEntry(item.Path);
        var targetExpanded = NormalizePathEntry(SafeExpand(item.Path));
        var parts = raw!.Split(';');
        var kept = parts.Where(p =>
        {
            var n = NormalizePathEntry(p);
            if (n.Length == 0) return true; // 保留空段，不改动无关内容
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
            // 备份成功是覆写 PATH 的前置条件：无备份即无法恢复被误删的条目
            r.Failed++; r.FailedItems.Add(item.Path);
            r.Warnings.Add($"PATH 备份失败，已中止移除（无备份即无法恢复）: {item.Path}");
            log.AppendLine($"⛔ PATH 备份失败，已中止移除: {item.Path}");
            return;
        }

        // 保持原值类型（默认 REG_EXPAND_SZ），并广播环境变量变更通知
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
            // 同会话删多条时逐次备份：固定文件名会被后续备份覆盖，丢失含完整原值的第一份。
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

    /// <summary>若目标文件已存在则追加序号，避免覆盖已有备份。</summary>
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

    /// <summary>广播 WM_SETTINGCHANGE("Environment")，使资源管理器/新进程感知 PATH 变更。</summary>
    private static void BroadcastEnvironmentChange()
    {
        try
        {
            SendMessageTimeout((IntPtr)HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero,
                "Environment", SMTO_ABORTIFHUNG, 3000, out _);
        }
        catch { /* 仅通知，失败不影响删除结果 */ }
    }

    // ---------------- 防火墙规则（netsh.exe）----------------

    private static void CleanFirewallRule(ResidualItem item, ResidualCleanResult r, StringBuilder log)
    {
        var ruleName = string.IsNullOrWhiteSpace(item.Payload) ? item.Path : item.Payload!;
        // 规则名为任意字符串：内嵌引号可构造 name=all 等参数逃逸，一次删光全部规则，必须拒绝
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

    // ---------------- 系统命令执行 ----------------

    /// <summary>名称是否含双引号（拼接进命令行会逃逸参数边界，扩大删除范围）。</summary>
    private static bool HasUnsafeQuote(string name) => name.Contains('"');

    /// <summary>执行系统命令（sc/schtasks/netsh），返回是否成功与输出摘要。失败写内部日志。</summary>
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

            // 异步读取两路输出，避免“子进程挂起 + 同步 ReadToEnd 永久阻塞”使 30s 超时形同虚设
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(30000))
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"{fileName} {arguments} 执行超时(30s)，已强制结束");
                return (false, "命令执行超时");
            }
            proc.WaitForExit(); // 确保异步输出缓冲区读尽
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

    // ---------------- 注册表删除 ----------------

    /// <summary>判断注册表键是否存在（用于区分“备份失败”与“键本就不存在”）。</summary>
    internal static bool RegistryKeyExists(string fullPath)
    {
        try
        {
            var (hive, rel) = SplitRegistryPath(fullPath);
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(rel);
            return key is not null;
        }
        catch { return true; } // 无法确认时保守视为存在，走“备份失败则中止”的安全路径
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

