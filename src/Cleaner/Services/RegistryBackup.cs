using System.Diagnostics;
using System.IO;
using BeeXCleaner.Infrastructure;

namespace BeeXCleaner.Services;

/// <summary>
/// Before deleting the registry, use reg.exe to export a .reg backup entry by entry, so you can restore it from the backup in case of an issue.
/// When you delete a registry value, the entire key it belongs to is backed up (reg.exe cannot export a single value).
/// </summary>
public static class RegistryBackup
{
    /// <summary>
    /// Exports the registry key to the session backup directory and returns the path to the .reg file; returns null on failure.
    /// <paramref name="fullKeyPath"/> in the form of HKEY_LOCAL_MACHINE\SOFTWARE\....
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
            // After redirection, both outputs must be continuously consumed; otherwise, if the child process's output exceeds the pipe buffer, it will block and be erroneously terminated by the 15-second timeout, resulting in a "false failure."
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(15000))
            {
                // Timeout: Terminate the hung reg.exe process to prevent process leaks and subsequent exceptions when accessing ExitCode
                try { proc.Kill(entireProcessTree: true); } catch { }
                AppLogger.Warn($"注册表导出超时(15s): {fullKeyPath}");
                return null;
            }

            if (proc.ExitCode == 0 && File.Exists(filePath))
                return filePath;

            // Key does not exist or export failed: Not critical (may have been deleted by the uninstaller); simply logged.
            AppLogger.Warn($"注册表导出返回码 {proc.ExitCode}: {fullKeyPath}");
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"注册表备份异常: {fullKeyPath}", ex);
            return null;
        }
    }

    /// <summary>Converts a full root name to a shorthand root (HKLM/HKCU) accepted by reg.exe. Returns null if not supported. </summary>
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
        // Truncate file names that are too long (retaining the more distinctive subkey names at the end)
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
