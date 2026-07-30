using System.Globalization;
using System.IO;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// 枚举本机已安装的 Win32 程序（读取注册表 Uninstall 项）。
/// </summary>
public sealed class ProgramScanner
{
    private const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// 扫描全部已安装程序，并去重。
    /// </summary>
    public List<InstalledProgram> Scan()
    {
        var results = new List<InstalledProgram>();

        // HKLM 64 位视图 + 32 位视图（WOW6432Node）
        ReadRoot(RegistryHive.LocalMachine, RegistryView.Registry64, results);
        ReadRoot(RegistryHive.LocalMachine, RegistryView.Registry32, results);
        // 当前用户：HKCU 的 Uninstall 不做 WOW64 重定向，64/32 视图指向同一物理键，只扫一次
        ReadRoot(RegistryHive.CurrentUser, RegistryView.Registry64, results);

        return Deduplicate(results);
    }

    private static void ReadRoot(RegistryHive hive, RegistryView view, List<InstalledProgram> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstallKey = baseKey.OpenSubKey(UninstallSubKey);
            if (uninstallKey is null) return;

            foreach (var subName in uninstallKey.GetSubKeyNames())
            {
                try
                {
                    using var app = uninstallKey.OpenSubKey(subName);
                    if (app is null) continue;

                    var program = Parse(app, subName, hive, view);
                    if (program is not null)
                        sink.Add(program);
                }
                catch
                {
                    // 单个子项异常忽略，继续枚举
                }
            }
        }
        catch
        {
            // 视图不存在或无权限，忽略
        }
    }

    private static InstalledProgram? Parse(RegistryKey app, string keyName, RegistryHive hive, RegistryView view)
    {
        var name = app.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // 过滤系统组件与更新补丁
        if (ReadInt(app, "SystemComponent") == 1)
            return null;

        var releaseType = app.GetValue("ReleaseType") as string;
        if (!string.IsNullOrEmpty(releaseType) &&
            (releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase)
             || releaseType.Equals("Update", StringComparison.OrdinalIgnoreCase)
             || releaseType.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;

        // 系统更新条目（KBxxxxxx）
        if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
            && name.Length > 2 && char.IsDigit(name[2]))
            return null;

        var uninstall = app.GetValue("UninstallString") as string;
        var quiet = app.GetValue("QuietUninstallString") as string;
        var windowsInstaller = ReadInt(app, "WindowsInstaller") == 1;

        // 既无卸载串、又非 MSI 的空条目通常没有意义
        var isMsiGuid = LooksLikeGuid(keyName);
        if (string.IsNullOrWhiteSpace(uninstall)
            && string.IsNullOrWhiteSpace(quiet)
            && !windowsInstaller
            && !isMsiGuid)
            return null;

        long sizeBytes = 0;
        var estKb = ReadInt(app, "EstimatedSize");
        if (estKb > 0) sizeBytes = (long)estKb * 1024;

        var installLocation = (app.GetValue("InstallLocation") as string)?.Trim().Trim('"');

        return new InstalledProgram
        {
            DisplayName = name!.Trim(),
            Publisher = (app.GetValue("Publisher") as string)?.Trim(),
            DisplayVersion = (app.GetValue("DisplayVersion") as string)?.Trim(),
            InstallLocation = string.IsNullOrWhiteSpace(installLocation) ? null : installLocation,
            UninstallString = uninstall,
            QuietUninstallString = quiet,
            DisplayIcon = (app.GetValue("DisplayIcon") as string)?.Trim(),
            UrlInfoAbout = app.GetValue("URLInfoAbout") as string,
            InstallDate = ParseInstallDate(app.GetValue("InstallDate") as string),
            SizeBytes = sizeBytes,
            Source = ProgramSource.Win32,
            Hive = hive,
            View = view,
            RegistrySubKeyPath = UninstallSubKey,
            RegistryKeyName = keyName,
            MsiProductCode = (isMsiGuid || windowsInstaller) && LooksLikeGuid(keyName) ? keyName : null
        };
    }

    private static int ReadInt(RegistryKey key, string name)
    {
        try
        {
            var v = key.GetValue(name);
            return v switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var r) => r,
                _ => 0
            };
        }
        catch { return 0; }
    }

    private static DateTime? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        // 常见格式 yyyyMMdd
        if (raw.Length == 8 && DateTime.TryParseExact(
                raw, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2))
            return d2;
        return null;
    }

    private static bool LooksLikeGuid(string s)
    {
        s = s.Trim();
        if (s.Length < 2 || s[0] != '{' || s[^1] != '}') return false;
        return Guid.TryParse(s.Trim('{', '}'), out _);
    }

    /// <summary>
    /// 去重：同名同版本仅保留一条；估算大小取较大值。
    /// </summary>
    private static List<InstalledProgram> Deduplicate(List<InstalledProgram> items)
    {
        var map = new Dictionary<string, InstalledProgram>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in items)
        {
            var key = $"{p.DisplayName}\u0000{p.DisplayVersion}";
            if (map.TryGetValue(key, out var existing))
            {
                if (p.SizeBytes > existing.SizeBytes)
                    existing.SizeBytes = p.SizeBytes;
            }
            else
            {
                map[key] = p;
            }
        }

        return map.Values
            .OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
