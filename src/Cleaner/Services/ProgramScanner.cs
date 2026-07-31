using System.Globalization;
using System.IO;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// List the Win32 programs installed on this computer (by reading the "Uninstall" registry entries).
/// </summary>
public sealed class ProgramScanner
{
    private const string UninstallSubKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>
    /// Scan all installed programs and remove duplicates.
    /// </summary>
    public List<InstalledProgram> Scan()
    {
        var results = new List<InstalledProgram>();

        // HKLM 64-bit View + 32-bit View (WOW6432Node)
        ReadRoot(RegistryHive.LocalMachine, RegistryView.Registry64, results);
        ReadRoot(RegistryHive.LocalMachine, RegistryView.Registry32, results);
        // Current user: HKCU's "Uninstall" does not perform WOW64 redirection; the 64-bit and 32-bit views point to the same physical key, and are scanned only once.
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
                    // Ignore a single exception in a subitem and continue enumeration
                }
            }
        }
        catch
        {
            // The view does not exist or you do not have permission; ignore
        }
    }

    private static InstalledProgram? Parse(RegistryKey app, string keyName, RegistryHive hive, RegistryView view)
    {
        var name = app.GetValue("DisplayName") as string;
        if (string.IsNullOrWhiteSpace(name))
            return null;

        // Filtration System Components and Update Patches
        if (ReadInt(app, "SystemComponent") == 1)
            return null;

        var releaseType = app.GetValue("ReleaseType") as string;
        if (!string.IsNullOrEmpty(releaseType) &&
            (releaseType.Equals("Security Update", StringComparison.OrdinalIgnoreCase)
             || releaseType.Equals("Update", StringComparison.OrdinalIgnoreCase)
             || releaseType.Equals("Hotfix", StringComparison.OrdinalIgnoreCase)))
            return null;

        // System Update Article (KBxxxxxx)
        if (name.StartsWith("KB", StringComparison.OrdinalIgnoreCase)
            && name.Length > 2 && char.IsDigit(name[2]))
            return null;

        var uninstall = app.GetValue("UninstallString") as string;
        var quiet = app.GetValue("QuietUninstallString") as string;
        var windowsInstaller = ReadInt(app, "WindowsInstaller") == 1;

        // Empty entries that are neither uninstallation strings nor MSI files are usually meaningless.
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
        // Common format: yyyyMMdd
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
    /// Duplicate removal: Only one entry with the same name and version is retained; the estimated size is taken as the larger value.
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
