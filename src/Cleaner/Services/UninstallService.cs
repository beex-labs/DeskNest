using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>Uninstallation Determination Results (Exit Code Semantic Classification).</summary>
public enum UninstallOutcome
{
    /// <summary>Success. </summary>
    Success,
    /// <summary>Success, but a restart is required to complete the process.</summary>
    RebootRequired,
    /// <summary>The target no longer exists; it is considered uninstalled.</summary>
    AlreadyRemoved,
    /// <summary>User cancellation. </summary>
    UserCancelled,
    /// <summary>Unknown exit code; unable to confirm completion (residual files will not be automatically cleaned up).</summary>
    Uncertain,
    /// <summary> Failed. </summary>
    Failed
}

/// <summary>Uninstallation results. </summary>
public sealed record UninstallResult(bool Success, string Message, UninstallOutcome Outcome = UninstallOutcome.Success)
{
    public static UninstallResult Ok(string msg = "") => new(true, msg, UninstallOutcome.Success);
    public static UninstallResult Fail(string msg) => new(false, msg, UninstallOutcome.Failed);

    /// <summary>Constructs the result based on the exit code semantics; for the Success class, set Success=true. </summary>
    public static UninstallResult From(UninstallOutcome outcome, string msg = "")
        => new(outcome is UninstallOutcome.Success or UninstallOutcome.RebootRequired or UninstallOutcome.AlreadyRemoved,
            msg, outcome);
}

/// <summary>
/// Responsible for performing uninstallation: normal uninstallation, silent uninstallation, and forced removal (registry + installation directory).
/// </summary>
public sealed partial class UninstallService
{
    /// <summary>
    /// Perform uninstallation. Attempt silent uninstallation when `silent=true`.
    /// </summary>
    public async Task<UninstallResult> UninstallAsync(InstalledProgram program, bool silent)
    {
        if (program.Source == ProgramSource.Uwp)
            return UninstallResult.Fail("UWP 应用请使用 UWP 卸载通道。");

        // In silent mode, the silent uninstallation string takes precedence; in non-silent mode, the standard uninstallation string takes precedence,
        // However, if the program only provides `QuietUninstallString`, use it to fulfill the uninstallation promise made by `CanNormalUninstall`.
        string? command = silent && !string.IsNullOrWhiteSpace(program.QuietUninstallString)
            ? program.QuietUninstallString
            : !string.IsNullOrWhiteSpace(program.UninstallString)
                ? program.UninstallString
                : program.QuietUninstallString;

        string fileName;
        string arguments;

        // MSI Products: Customizable, Silent Operation
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

            // If it is `msiexec` and silent execution is required, add the silent parameters.
            // Silent and "norestart" options must be specified separately: If the command line includes /qn but lacks /norestart,
            // When msiexec encounters a package that requires a restart in fully silent mode, it will automatically restart the system without displaying any prompts.
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

        // Record the set of processes before startup to identify child processes spawned by the uninstaller
        var beforePids = SnapshotProcessIds();
        var result = await RunProcessAsync(fileName, arguments, silent).ConfigureAwait(false);

        // Critical Fix (Issue 3): Many uninstallers are "launchers"—the main process exits immediately, and the process that actually performs the uninstallation
        // The child process is still running. We wait here for the uninstallation process to actually complete to avoid scanning for residual files too early.
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
                UseShellExecute = true // Allow calls to uninstallers that require UAC
            };

            using var proc = Process.Start(psi);
            if (proc is null)
                return UninstallResult.Fail("无法启动卸载程序。");

            // In silent mode, the uninstaller has no visible interface, and when it is suspended (with the dialog box hidden and waiting for input), the user cannot intervene:
            // A 10-minute timeout fallback; if the timeout occurs, force termination and return "Unknown" to prevent the main interface from becoming permanently locked due to batch uninstallation.
            // The interaction mode is still in an infinite wait—the user is currently interacting with the uninstaller UI, so we cannot interrupt the process on the user's behalf.
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

            // Semantic Classification of Uninstaller Exit Codes (6.3): Known codes are explicitly classified; unknown codes are uniformly marked as “undetermined,”
            // The upper-level system refuses to automatically clean up residual data to prevent accidental deletion after mistaking a failure for a success.
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
    /// Uninstaller Exit Code Semantic Mapping (6.3): 0 → Success; 3010 → Requires restart; 1605/1614 → Uninstalled; 1602 → Canceled; All others → Undetermined.
    /// Extract it into a separate method to facilitate self-checking for coverage.
    /// </summary>
    public static UninstallOutcome MapExitCode(int code) => code switch
    {
        0 => UninstallOutcome.Success,
        3010 => UninstallOutcome.RebootRequired,
        1605 or 1614 => UninstallOutcome.AlreadyRemoved,
        1602 => UninstallOutcome.UserCancelled,
        _ => UninstallOutcome.Uncertain
    };

    /// <summary>Snapshot of all current process IDs. </summary>
    private static HashSet<int> SnapshotProcessIds()
    {
        var set = new HashSet<int>();
        try { foreach (var p in Process.GetProcesses()) { set.Add(p.Id); p.Dispose(); } }
        catch { /* Ignore */ }
        return set;
    }

    /// <summary>
    /// Wait for the uninstallation to be fully complete: Poll until “all child processes derived from the uninstaller have exited” or “the program’s registry uninstall entry has been removed,”
    /// Includes a total timeout (10 minutes) to prevent an infinite wait.
    /// </summary>
    private static async Task WaitForCompletionAsync(InstalledProgram program, HashSet<int> beforePids)
    {
        var installLoc = program.InstallLocation?.TrimEnd('\\', '/');
        var tempPath = Path.GetTempPath().TrimEnd('\\', '/');
        var deadline = DateTime.UtcNow.AddMinutes(10);
        var clearStreak = 0;

        // Allow time for child processes spawned by the uninstaller to appear, to avoid the "main process exiting immediately and marking the process as complete" issue.
        await Task.Delay(2000).ConfigureAwait(false);

        while (DateTime.UtcNow < deadline)
        {
            // The registry uninstall entry has been removed → Uninstallation is considered complete; return immediately
            if (!UninstallKeyExists(program))
                return;

            if (!AnyUninstallerRunning(beforePids, installLoc, tempPath))
            {
                if (++clearStreak >= 2) return; // If there are no unloader processes for two consecutive times, the process is considered to have ended.
            }
            else
            {
                clearStreak = 0;
            }

            await Task.Delay(700).ConfigureAwait(false);
        }
    }

    /// <summary>Are there still "newly appearing" uninstaller-related processes running during uninstallation? </summary>
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
                    if (beforePids.Contains(p.Id)) continue; // Focus only on processes newly created during uninstallation

                    var name = p.ProcessName.ToLowerInvariant();
                    if (name is "msiexec" or "uninstall" or "uninst" or "un_a" or "au_"
                        or "setup" or "unins000" or "unins001" or "unins002")
                        return true;

                    string? path = null;
                    try { path = p.MainModule?.FileName; } catch { /* No permissions or logged out */ }
                    if (string.IsNullOrEmpty(path)) continue;

                    // Add a directory separator and compare again: This prevents "Temp2\x.exe" from being incorrectly flagged by the "Temp" prefix, which would cause the system to idle until the 10-minute timeout.
                    if (path.StartsWith(tempPath + "\\", StringComparison.OrdinalIgnoreCase)) return true;
                    if (!string.IsNullOrEmpty(installLoc)
                        && path.StartsWith(installLoc! + "\\", StringComparison.OrdinalIgnoreCase)) return true;

                    var pl = path.ToLowerInvariant();
                    if (pl.Contains("uninst") || pl.Contains("\\unins")) return true;
                }
                catch { /* Ignore Exceptions in a Single Process */ }
            }
        }
        finally
        {
            foreach (var p in procs) { try { p.Dispose(); } catch { } }
        }
        return false;
    }

    /// <summary>Are the registry uninstallation entries for this program still present?</summary>
    private static bool UninstallKeyExists(InstalledProgram program)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var uninstallKey = baseKey.OpenSubKey(program.RegistrySubKeyPath);
            using var appKey = uninstallKey?.OpenSubKey(program.RegistryKeyName);
            return appKey is not null;
        }
        catch { return true; } // When an error occurs, assume it still exists; instead, use process signals to determine completion.
    }

    /// <summary>
    /// Force Uninstall: Directly removes the "Uninstall" registry key and, optionally, deletes the installation directory.
    /// To be used when the standard uninstallation fails or the program is incomplete.
    /// </summary>
    public UninstallResult ForceRemove(InstalledProgram program, bool deleteInstallFolder)
    {
        var sb = new StringBuilder();
        var anyOk = false;

        // 1) Delete the "Uninstall" subkey from the registry
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

        // 2) Optional: Delete the installation directory
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
    /// Parse the uninstall command line to extract the executable file path and parameters.
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

        // No quotes: If the string contains .exe, split it at the end of the first .exe
        var m = ExeSplitRegex().Match(commandLine);
        if (m.Success)
        {
            var exe = commandLine.Substring(0, m.Index + m.Length);
            var args = commandLine[(m.Index + m.Length)..].Trim();
            return (exe, args);
        }

        // Regression: Split by the first space
        var sp = commandLine.IndexOf(' ');
        return sp < 0
            ? (commandLine, string.Empty)
            : (commandLine[..sp], commandLine[(sp + 1)..].Trim());
    }

    /// <summary>
    /// Remove security checks. Only paths located on [this computer's local disk] that are not in the operating system's critical root directory may be deleted.
    /// - Block UNC paths and network drives / NAS (to protect users' network storage).
    /// - Do not delete the root of the drive or approximately 12 key system directories [as a whole] (their subdirectories may still be deleted).
    /// All local directories other than these will be allowed through and immediately and forcibly deleted; no "skips" will be made.
    /// </summary>
    public static bool IsSafeToDelete(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        // Limited to the local disk on this machine: Network locations (UNC / mapped network drives / NAS) are strictly prohibited.
        if (FileSystemUtil.IsNetworkPath(path)) return false;

        string full;
        try { full = StripLongPathPrefix(Path.GetFullPath(path)).TrimEnd('\\'); }
        catch { return false; }

        if (full.Length <= 3) return false; // Drive letter, such as C:\

        // Catastrophic Root Directory: Deleting this directory entirely will destroy the system or erase all user data; this is never permitted.
        // Note: This only blocks the "directory itself"; any subdirectories within it (actual software remnants) can still be deleted.
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
            Path.Combine(sysDrive, "Boot"),        // Startup Configuration
            Path.Combine(sysDrive, "Recovery"),    // Recovery Environment
            Path.Combine(sysDrive, "Windows.old")  // Backups of the old system are not automatically deleted by default.
        };

        foreach (var p in criticalRoots)
        {
            if (string.IsNullOrEmpty(p)) continue;
            if (full.Equals(p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // WindowsApps (the UWP package directory) is centrally managed by the AppX Deployment Service: Bypassing it to delete files directly will result in
        // In the case of an inconsistency where a package is registered in the deployment repository but its files are missing, this directory and all its subdirectories will be rejected without exception.
        if (IsUnderWindowsApps(full)) return false;

        return true;
    }

    /// <summary>Is the path located under Program Files\WindowsApps (UWP package library), including the directory itself? </summary>
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

    /// <summary>Remove the \\?\ / \\?\UNC\ long path prefixes: this prevents them from bypassing the string comparison safeguards for critical directories.</summary>
    private static string StripLongPathPrefix(string path)
    {
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    /// <summary>
    /// File-Level System Protection (6.5): Prevents the deletion of files located in critical system directories such as Windows, System32, and SysWOW64.
    /// Network drives and NAS devices are also rejected across the board. This is complementary to <see cref="IsSafeToDelete"/> (which primarily blocks the root directory).
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
            Path.Combine(sysDrive, "Boot"),       // BIOS boot configuration (BCD, etc.); if deleted, the system will fail to boot
            Path.Combine(sysDrive, "Recovery"),   // Recovery Environment
            Path.Combine(sysDrive, "Windows.old") // Backup of the Old System
        };

        foreach (var d in protectedDirs)
        {
            if (string.IsNullOrEmpty(d)) continue;
            var dir = d.TrimEnd('\\');
            if (full.Equals(dir, StringComparison.OrdinalIgnoreCase)
                || full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // Key boot and memory management files located in the system root directory: Delaying their deletion until after a reboot can bypass file locks; deleting them will prevent the system from booting.
        foreach (var f in new[] { "bootmgr", "BOOTNXT", "pagefile.sys", "hiberfil.sys", "swapfile.sys" })
        {
            if (full.Equals(Path.Combine(sysDrive, f), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        // UWP package files are also rejected (complementing the directory-level blocking provided by `IsSafeToDelete`)
        if (IsUnderWindowsApps(full)) return false;

        return true;
    }

    /// <summary>
    /// Identify common cloud sync root directories (OneDrive, Dropbox, Google Drive).
    /// Although these directories are local paths, deleting them entirely will be synchronized to the cloud, so a strong warning is required (but not a hard prohibition).
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
