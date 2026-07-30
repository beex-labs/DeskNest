using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>卸载判定结果（退出码语义分级）。</summary>
public enum UninstallOutcome
{
    /// <summary>成功。</summary>
    Success,
    /// <summary>成功，但需重启完成。</summary>
    RebootRequired,
    /// <summary>目标已不存在，视为已卸载。</summary>
    AlreadyRemoved,
    /// <summary>用户取消。</summary>
    UserCancelled,
    /// <summary>未知退出码，无法确认是否完成（不自动清理残留）。</summary>
    Uncertain,
    /// <summary>失败。</summary>
    Failed
}

/// <summary>卸载结果。</summary>
public sealed record UninstallResult(bool Success, string Message, UninstallOutcome Outcome = UninstallOutcome.Success)
{
    public static UninstallResult Ok(string msg = "") => new(true, msg, UninstallOutcome.Success);
    public static UninstallResult Fail(string msg) => new(false, msg, UninstallOutcome.Failed);

    /// <summary>由退出码语义构造结果；成功类 Outcome 置 Success=true。</summary>
    public static UninstallResult From(UninstallOutcome outcome, string msg = "")
        => new(outcome is UninstallOutcome.Success or UninstallOutcome.RebootRequired or UninstallOutcome.AlreadyRemoved,
            msg, outcome);
}

/// <summary>
/// 负责执行卸载：正常卸载、静默卸载、以及强制删除（注册表 + 安装目录）。
/// </summary>
public sealed partial class UninstallService
{
    /// <summary>
    /// 执行卸载。silent=true 时尝试静默卸载。
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, bool silent)
    {
        if (program.Source == ProgramSource.Uwp)
            return UninstallResult.Fail("UWP 应用请使用 UWP 卸载通道。");

        // 静默时优先静默卸载串；非静默时优先普通卸载串，
        // 但若程序只提供了 QuietUninstallString，也用它兑现 CanNormalUninstall 的卸载承诺。
        string? command = silent && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : !string.IsNullOrWhiteSpace(program.UninstallString)
                ? program.UninstallString
                : program.QuietUninstallString;

        string fileName;
        string arguments;

        // MSI 产品：自行构造，可控制静默
        if (program.MsiProductCode is not null &&
            (string.IsNullOrWhiteSpace(command) || command.Contains("msiexec", StringComparison.OrdinalIgnoreCase)))
        {
            fileName = "msiexec.exe";
            arguments = $"/x {program.MsiProductCode}" + (silent ? " /quiet /norestart" : "");
        }
        else
        {
            if (string.IsNullOrWhiteSpace(command))
                return UninstallResult.Fail("该程序没有提供卸载命令，请使用“强制删除”。");

            var (exe, args) = ParseCommandLine(command);
            if (string.IsNullOrWhiteSpace(exe))
                return UninstallResult.Fail("无法解析卸载命令。");

            // 若为 msiexec 且需要静默，则补充静默参数。
            // 静默与抑制重启必须分别补齐：命令行自带 /qn 时若缺 /norestart，
            // msiexec 全静默模式下遇到需重启的包会不弹任何提示直接自动重启系统。
            if (silent && exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                if (!args.Contains("/quiet", StringComparison.OrdinalIgnoreCase)
                    && !args.Contains("/qn", StringComparison.OrdinalIgnoreCase))
                    args += " /quiet";
                if (!args.Contains("/norestart", StringComparison.OrdinalIgnoreCase)
                    && !args.Contains("REBOOT=", StringComparison.OrdinalIgnoreCase))
                    args += " /norestart";
            }

            fileName = exe;
            arguments = args;
        }

        // 记录启动前的进程集合，用于识别卸载器派生的子进程
        var beforePids = SnapshotProcessIds();
        var result = await RunProcessAsync(fileName, arguments, silent).ConfigureAwait(false);

        // 关键修复(问题3)：很多卸载器是“启动器”——主进程会立即退出，真正执行卸载的
        // 子进程仍在运行。此处等待卸载过程真正结束，避免过早去扫描残留。
        if (result.Success)
            await WaitForCompletionAsync(program, beforePids).ConfigureAwait(false);

        return result;
    }

    private static async Task<UninstallResult> RunProcessAsync(string fileName, string arguments, bool silent)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true // 允许调用需要 UAC 的卸载器
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return UninstallResult.Fail("无法启动卸载程序。");

            // 静默模式下卸载器无任何可见界面，挂起（隐藏对话框等待输入）时用户无从干预：
            // 10 分钟超时兜底，超时强杀并返回“不确定”，避免批量卸载永久锁死主界面。
            // 交互模式仍无限等待——用户正在操作卸载器 UI，不能替用户掐断。
            if (silent)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
                try
                {
                    await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return UninstallResult.From(UninstallOutcome.Uncertain,
                        "卸载器执行超时（10 分钟），已强制结束，无法确认卸载是否完成。\n"
                        + "建议重新扫描软件列表，确认软件已消失后再清理残留。");
                }
            }
            else
            {
                await proc.WaitForExitAsync().ConfigureAwait(false);
            }

            // 卸载器退出码语义分级（6.3）：已知码明确判定，未知码一律标记为“不确定”，
            // 由上层拒绝自动清理残留，避免把失败当成功后误删。
            var outcome = MapExitCode(proc.ExitCode);
            var msg = outcome switch
            {
                UninstallOutcome.RebootRequired => "卸载完成，需要重启系统以完成清理。",
                UninstallOutcome.AlreadyRemoved => "该程序已不存在（视为已卸载）。",
                UninstallOutcome.UserCancelled => "用户取消了卸载。",
                UninstallOutcome.Uncertain =>
                    $"卸载器返回了未知结果（退出码 {proc.ExitCode}），BeeX Cleaner 无法确认卸载是否完成。\n"
                    + "建议重新扫描软件列表，确认软件已消失后再清理残留。",
                _ => ""
            };
            return UninstallResult.From(outcome, msg);
        }
        catch (Exception ex)
        {
            return UninstallResult.Fail($"启动卸载程序失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 卸载器退出码语义映射（6.3）：0→成功；3010→需重启；1605/1614→已卸载；1602→取消；其余→不确定。
    /// 提取为独立方法以便自检覆盖。
    /// </summary>
    public static UninstallOutcome MapExitCode(int code) => code switch
    {
        0 => UninstallOutcome.Success,
        3010 => UninstallOutcome.RebootRequired,
        1605 or 1614 => UninstallOutcome.AlreadyRemoved,
        1602 => UninstallOutcome.UserCancelled,
        _ => UninstallOutcome.Uncertain
    };

    /// <summary>快照当前所有进程 ID。</summary>
    private static HashSet<int> SnapshotProcessIds()
    {
        var set = new HashSet<int>();
        try { foreach (var p in Process.GetProcesses()) { set.Add(p.Id); p.Dispose(); } }
        catch { /* 忽略 */ }
        return set;
    }

    /// <summary>
    /// 等待卸载真正完成：轮询直到“卸载器派生的子进程全部退出”或“该程序的注册表卸载项消失”，
    /// 带总超时(10 分钟)以防无限等待。
    /// </summary>
    private static async Task WaitForCompletionAsync(InstalledProgram program, HashSet<int> beforePids)
    {
        var installLoc = program.InstallLocation?.TrimEnd('\\', '/');
        var tempPath = Path.GetTempPath().TrimEnd('\\', '/');
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var clearStreak = 0;

        // 给卸载器派生的子进程留出出现的时间，避免“主进程秒退即判完成”
        await Task.Delay(2000).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline)
        {
            // 注册表卸载项已被移除 → 视为卸载完成，立即返回
            if (!UninstallKeyExists(program))
                return;

            if (!AnyUninstallerRunning(beforePids, installLoc, tempPath))
            {
                if (++clearStreak >= 2) return; // 连续两次无卸载器进程即认为结束
            }
            else
            {
                clearStreak = 0;
            }

            await Task.Delay(700).ConfigureAwait(false);
        }
    }

    /// <summary>卸载期间是否仍有“新出现的”卸载器相关进程在运行。</summary>
    private static bool AnyUninstallerRunning(HashSet<int> beforePids, string? installLoc, string tempPath)
    {
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch { return false; }

        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (beforePids.Contains(p.Id)) continue; // 只关注卸载期间新派生的进程

                    var name = p.ProcessName.ToLowerInvariant();
                    if (name is "msiexec" or "uninstall" or "uninst" or "un_a" or "au_"
                        or "setup" or "unins000" or "unins001" or "unins002")
                        return true;

                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* 无权限或已退出 */ }
                    if (string.IsNullOrEmpty(path)) continue;

                    // 补目录分隔符再比较：避免 Temp2\x.exe 被 Temp 前缀误判，空转至 10 分钟超时
                    if (path.StartsWith(tempPath + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                    if (!string.IsNullOrEmpty(installLoc)
                        && path.StartsWith(installLoc! + "\\", StringComparison.OrdinalIgnoreCase)) return true;

                    var pl = path.ToLowerInvariant();
                    if (pl.Contains("uninst") || pl.Contains("\\unins")) return true;
                }
                catch { /* 单个进程异常忽略 */ }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        return false;
    }

    /// <summary>该程序的注册表卸载项是否仍存在。</summary>
    private static bool UninstallKeyExists(InstalledProgram program)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var uninstallKey = baseKey.OpenSubKey(program.RegistrySubKeyPath);
            using var appKey = uninstallKey?.OpenSubKey(program.RegistryKeyName);
            return appKey is not null;
        }
        catch { return true; } // 出错时保守认为仍存在，改由进程信号判定完成
    }

    /// <summary>
    /// 强制删除：直接移除注册表 Uninstall 项，并可选删除安装目录。
    /// 用于常规卸载失败或程序残缺时。
    /// </summary>
    public UninstallResult ForceRemove(InstalledProgram program, bool deleteInstallFolder)
    {
        var sb = new StringBuilder();
        var anyOk = false;

        // 1) 删除注册表 Uninstall 子项
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var uninstallKey = baseKey.OpenSubKey(program.RegistrySubKeyPath, writable: true);
            if (uninstallKey is not null &&
                uninstallKey.GetSubKeyNames().Contains(program.RegistryKeyName, StringComparer.OrdinalIgnoreCase))
            {
                uninstallKey.DeleteSubKeyTree(program.RegistryKeyName, throwOnMissingSubKey: false);
                sb.AppendLine("✔ 已删除注册表卸载项");
                anyOk = true;
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"✗ 删除注册表项失败: {ex.Message}");
        }

        // 2) 可选：删除安装目录
        if (deleteInstallFolder && !string.IsNullOrWhiteSpace(program.InstallLocation))
        {
            var dir = program.InstallLocation!;
            if (IsSafeToDelete(dir) && Directory.Exists(dir))
            {
                try
                {
                    FileSystemUtil.ForceDeleteDirectory(dir);
                    sb.AppendLine($"✔ 已删除安装目录: {dir}");
                    anyOk = true;
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"✗ 删除安装目录失败: {ex.Message}");
                }
            }
        }

        return anyOk
            ? UninstallResult.Ok(sb.ToString().Trim())
            : UninstallResult.Fail(sb.Length > 0 ? sb.ToString().Trim() : "没有可删除的内容。");
    }

    /// <summary>
    /// 解析卸载命令行，拆分出可执行文件路径与参数。
    /// </summary>
    public static (string exe, string args) ParseCommandLine(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.Length == 0) return (string.Empty, string.Empty);

        if (commandLine[0] == '"')
        {
            var end = commandLine.IndexOf('"', 1);
            if (end > 0)
            {
                var exe = commandLine.Substring(1, end - 1);
                var args = commandLine[(end + 1)..].Trim();
                return (exe, args);
            }
            return (commandLine.Trim('"'), string.Empty);
        }

        // 未加引号：若包含 .exe，则以第一个 .exe 结尾处切分
        var m = ExeSplitRegex().Match(commandLine);
        if (m.Success)
        {
            var exe = commandLine.Substring(0, m.Index + m.Length);
            var args = commandLine[(m.Index + m.Length)..].Trim();
            return (exe, args);
        }

        // 退化：按第一个空格切分
        var sp = commandLine.IndexOf(' ');
        return sp < 0
            ? (commandLine, string.Empty)
            : (commandLine[..sp], commandLine[(sp + 1)..].Trim());
    }

    /// <summary>
    /// 删除安全检查。仅允许删除【本机本地磁盘】上、且非操作系统关键根目录的路径。
    /// - 拒绝 UNC 与网络盘 / NAS（保护用户的网络存储）。
    /// - 拒绝盘符根与约 12 个系统关键目录【整体】删除（其子目录仍可删）。
    /// 除此之外的本地目录一律放行，直接强制删除，不做任何“跳过”。
    /// </summary>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // 仅限本机本地磁盘：网络位置（UNC / 映射网络盘 / NAS）一律禁止
        if (FileSystemUtil.IsNetworkPath(path)) return false;

        string full;
        try { full = StripLongPathPrefix(Path.GetFullPath(path)).TrimEnd('\\'); }
        catch { return false; }

        if (full.Length <= 3) return false; // 盘符根，如 C:\

        // 灾难性根目录：整体删除会毁坏系统或抹掉全部用户数据，永不允许。
        // 注意：这里只拦截“目录本身”，其下的子目录（真实软件残留）仍可删除。
        var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrEmpty(sysDrive)) sysDrive = @"C:\";

        var criticalRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),          // System32
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),       // SysWOW64
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),     // Program Files\Common Files
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),  // Program Files (x86)\Common Files
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), // ProgramData
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),  // AppData\Local
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),       // AppData\Roaming
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"C:\Users",
            Path.Combine(sysDrive, "Boot"),        // 启动配置
            Path.Combine(sysDrive, "Recovery"),    // 恢复环境
            Path.Combine(sysDrive, "Windows.old")  // 旧系统备份，默认不自动删除
        };

        foreach (var p in criticalRoots)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (full.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // WindowsApps（UWP 包目录）由 Appx 部署服务统一管理：绕开它直接删文件会造成
        // “包在部署库中已注册但文件缺失”的不一致状态，本目录及其全部子目录一律拒绝。
        if (IsUnderWindowsApps(full)) return false;

        return true;
    }

    /// <summary>路径是否位于 Program Files\WindowsApps（UWP 包库）之下（含目录本身）。</summary>
    private static bool IsUnderWindowsApps(string full)
    {
        foreach (var pf in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var wa = Path.Combine(pf.TrimEnd('\\'), "WindowsApps");
            if (full.Equals(wa, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(wa + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>去除 \\?\ / \\?\UNC\ 长路径前缀：防止其绕过关键目录的字符串比对护栏。</summary>
    private static string StripLongPathPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// 文件级系统保护（6.5）：禁止删除位于 Windows / System32 / SysWOW64 等关键系统目录下的文件。
    /// 网络盘 / NAS 也一律拒绝。与 <see cref="IsSafeToDelete"/> 互补（后者主要拦截目录根）。
    /// </summary>
    public static bool IsSafeFileToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (FileSystemUtil.IsNetworkPath(path)) return false;

        string full;
        try { full = StripLongPathPrefix(Path.GetFullPath(path)); }
        catch { return false; }

        var sysDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (string.IsNullOrEmpty(sysDrive)) sysDrive = @"C:\";

        var protectedDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.System),    // System32
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), // SysWOW64
            Path.Combine(sysDrive, "Boot"),       // BIOS 引导配置（BCD 等），删除后系统无法启动
            Path.Combine(sysDrive, "Recovery"),   // 恢复环境
            Path.Combine(sysDrive, "Windows.old") // 旧系统备份
        };

        foreach (var d in protectedDirs)
        {
            if (string.IsNullOrEmpty(d)) continue;
            var dir = d.TrimEnd('\\');
            if (full.Equals(dir, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // 系统盘根下的启动/内存管理关键文件：重启延迟删除可绕过文件锁，删掉即无法开机
        foreach (var f in new[] { "bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
        {
            if (full.Equals(Path.Combine(sysDrive, f), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // UWP 包库文件同样拒绝（与 IsSafeToDelete 的目录级拦截互补）
        if (IsUnderWindowsApps(full)) return false;

        return true;
    }

    /// <summary>
    /// 识别常见云同步根目录（OneDrive / Dropbox / Google Drive）。
    /// 这类目录虽为本地路径，但整体删除会同步到云端，需强提示（不硬禁）。
    /// </summary>
    public static bool IsCloudSyncRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch { return false; }

        foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var od = Environment.GetEnvironmentVariable(v);
            if (!string.IsNullOrEmpty(od) && full.Equals(od.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var leaf = Path.GetFileName(full);
        string[] names = { "OneDrive", "Dropbox", "Google Drive", "GoogleDrive" };
        return names.Any(n => leaf.StartsWith(n, StringComparison.OrdinalIgnoreCase));
    }

    [GeneratedRegex(@"\.exe\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExeSplitRegex();
}
