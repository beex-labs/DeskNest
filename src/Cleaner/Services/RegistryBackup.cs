using System.Diagnostics;
using System.IO;
using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Services;

/// <summary>
/// 删除注册表前用 reg.exe 逐项导出 .reg 备份，便于异常时从“备份恢复”还原。
/// 删除某个注册表值时会备份其所在整个键（reg.exe 无法只导出单个值）。
/// </summary>
public static class RegistryBackup
{
    /// <summary>
    /// 导出注册表键到会话备份目录，返回 .reg 文件路径；失败返回 null。
    /// <paramref name="fullKeyPath"/> 形如 HKEY_LOCAL_MACHINE\SOFTWARE\...。
    /// </summary>
    public static string? Export(string fullKeyPath, string backupFolder)
    {
        try
        {
            var regKey = ToRegExeKey(fullKeyPath);
            if (regKey is null) return null;

            Directory.CreateDirectory(backupFolder);
            var filePath = EnsureUnique(Path.Combine(backupFolder, MakeSafeFileName(fullKeyPath) + ".reg"));

            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"export \"{regKey}\" \"{filePath}\" /y",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            // 重定向后必须持续消费两路输出：否则子进程输出超过管道缓冲会阻塞，被 15s 超时误杀成“假失败”
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                // 超时：结束悬挂的 reg.exe，避免进程泄漏与后续 ExitCode 访问抛异常
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"注册表导出超时(15s): {fullKeyPath}");
                return null;
            }

            if (proc.ExitCode == 0 && File.Exists(filePath))
                return filePath;

            // 键不存在或导出失败：不算致命（可能已被卸载器删掉），仅记录。
            AppLogger.Warn($"注册表导出返回码 {proc.ExitCode}: {fullKeyPath}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"注册表备份异常: {fullKeyPath}", ex);
            return null;
        }
    }

    /// <summary>把完整根名转换为 reg.exe 接受的缩写根（HKLM/HKCU）。不支持则返回 null。</summary>
    private static string? ToRegExeKey(string fullPath)
    {
        const string hklm = "HKEY_LOCAL_MACHINE";
        const string hkcu = "HKEY_CURRENT_USER";
        if (fullPath.StartsWith(hklm, StringComparison.OrdinalIgnoreCase))
            return "HKLM" + fullPath[hklm.Length..];
        if (fullPath.StartsWith(hkcu, StringComparison.OrdinalIgnoreCase))
            return "HKCU" + fullPath[hkcu.Length..];
        return null;
    }

    private static string MakeSafeFileName(string fullPath)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = fullPath.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray();
        var name = new string(chars).Trim('_');
        // 文件名过长时截断（保留尾部更有辨识度的子键名）
        if (name.Length > 120) name = name[^120..];
        return name.Length == 0 ? "registry" : name;
    }

    private static string EnsureUnique(string filePath)
    {
        if (!File.Exists(filePath)) return filePath;
        var dir = Path.GetDirectoryName(filePath)!;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        var ext = Path.GetExtension(filePath);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return filePath;
    }
}
