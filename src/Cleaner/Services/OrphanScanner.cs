using System.IO;
using System.Runtime.InteropServices;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// Residual Scan (Entire System): Searches for entries where "software has been uninstalled but residual files remain":
/// ① Broken shortcut (target file has been deleted)
/// ② Orphaned uninstallation entries (Uninstall entries in the registry, but the installation directory or Program Files folder no longer exists)
/// ③ Orphaned App Paths (Registered executables have been deleted)
/// ④ Autostart entries (Run / RunOnce) that point to deleted files
/// ⑤ Orphaned folders (directories under "Program Files" that do not belong to any installed programs)
/// The first four categories use "the referenced local file does not exist" as the criterion; the fifth category compares the list of installed programs with the system whitelist,
/// By default, the "Require User Confirmation" checkbox is unchecked. Network and NAS locations are always ignored.
/// </summary>
public sealed class OrphanScanner
{
    private static readonly EnumerationOptions LnkEnum = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly EnumerationOptions TopEnum = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private const string UninstallBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallBase32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string AppPathsBase = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths";
    private const string AppPathsBase32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths";

    /// <summary>
    /// Legacy scan. When <paramref name="deep"/>=true, an extended scan is performed (scheduled tasks/services/PATH/firewall/file associations,
    /// (All use "The referenced file no longer exists" as the criterion; this option is unchecked by default.)
    /// </summary>
    public List<ResidualItem> Scan(bool deep = false)
    {
        var results = new List<ResidualItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        ScanBrokenShortcuts(results, seen);
        ScanDeadUninstallEntries(results, seen);
        ScanOrphanAppPaths(results, seen);
        ScanOrphanRunEntries(results, seen);
        ScanOrphanProgramFolders(results, seen);

        if (deep)
            results.AddRange(ExtendedScanner.ScanOrphans(includeFileAssociations: true));

        return results
            .OrderBy(r => r.Type)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Clean up (uses the shared forced cleanup tool; optionally cleans up sessions or terminates processes). </summary>
    public ResidualCleanResult Clean(IEnumerable<ResidualItem> items, bool secureErase = false,
        CleanupSession? session = null, bool killProcesses = false)
        => ResidualCleaner.Clean(items, secureErase, session, killProcesses);

    // ---------------- ① Broken Shortcuts ----------------

    private static void ScanBrokenShortcuts(List<ResidualItem> results, HashSet<string> seen)
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        }.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct();

        object? shell = CreateWshShell();
        try
        {
            foreach (var root in roots)
            {
                List<string> lnks;
                try { lnks = Directory.EnumerateFiles(root!, "*.lnk", LnkEnum).ToList(); }
                catch { continue; }

                foreach (var lnk in lnks)
                {
                    var target = ResolveShortcutTarget(shell, lnk);
                    if (string.IsNullOrWhiteSpace(target)) continue; // Skip non-file targets (URLs/store items)
                    if (!IsMissingLocalTarget(target)) continue;
                    if (!seen.Add("S:" + lnk)) continue;

                    long size = 0;
                    try { size = new FileInfo(lnk).Length; } catch { }
                    results.Add(new ResidualItem
                    {
                        Type = ResidualType.Shortcut,
                        Path = lnk,
                        SizeBytes = size,
                        MatchReason = $"目标已不存在：{target}",
                        Source = ResidualSource.Orphan
                    });
                }
            }
        }
        finally
        {
            // Explicitly release WScript.Shell RCW to prevent COM objects from accumulating in the GC after scanning a large number of .lnk files
            if (shell is not null)
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }

    // ---------------- ② Dead Uninstall Entries ----------------

    private static void ScanDeadUninstallEntries(List<ResidualItem> results, HashSet<string> seen)
    {
        var locations = new (RegistryHive hive, RegistryView view, string sub)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallBase),
            // 32-bit entries using a 64-bit view + explicit WOW6432Node path (consistent with other scans in this class);
            // If you overlay the WOW path using the Registry32 view, it will result in a double redirection to a nonexistent key, causing the 32-bit uninstallation entries to never be scanned.
            (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallBase32),
            (RegistryHive.CurrentUser,  RegistryView.Registry64, UninstallBase)
        };

        foreach (var (hive, view, sub) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var root = baseKey.OpenSubKey(sub);
                if (root is null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var app = root.OpenSubKey(name);
                        if (app is null) continue;

                        var display = app.GetValue("DisplayName") as string;
                        if (string.IsNullOrWhiteSpace(display)) continue;

                        var installLoc = (app.GetValue("InstallLocation") as string)?.Trim().Trim('"');
                        var icon = app.GetValue("DisplayIcon") as string;

                        string? why = null;
                        if (!string.IsNullOrWhiteSpace(installLoc) && IsMissingLocalTarget(installLoc))
                        {
                            why = $"安装目录已不存在：{installLoc}";
                        }
                        else if (!string.IsNullOrWhiteSpace(icon))
                        {
                            var exe = StripIconIndex(icon!);
                            if (exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                && IsMissingLocalTarget(exe))
                                why = $"程序文件已不存在：{exe}";
                        }

                        if (why is not null)
                            AddKey(results, seen, hive, $@"{sub}\{name}", $"死卸载项「{display}」— {why}");
                    }
                    catch { /* Single-Item Exclusion */ }
                }
            }
            catch { /* No permission or does not exist */ }
        }
    }

    // ---------------- ③ Orphaned App Paths ----------------

    private static void ScanOrphanAppPaths(List<ResidualItem> results, HashSet<string> seen)
    {
        var locations = new (RegistryHive hive, string sub)[]
        {
            (RegistryHive.LocalMachine, AppPathsBase),
            (RegistryHive.LocalMachine, AppPathsBase32),
            (RegistryHive.CurrentUser,  AppPathsBase)
        };

        foreach (var (hive, sub) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(sub);
                if (root is null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(name);
                        var def = k?.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(def)) continue;
                        var exe = StripIconIndex(def!);
                        if (IsMissingLocalTarget(exe))
                            AddKey(results, seen, hive, $@"{sub}\{name}", $"孤儿 App Paths「{name}」指向已删除文件：{exe}");
                    }
                    catch { }
                }
            }
            catch { }
        }
    }

    // ---------------- ④ Inactive Auto-Start Items ----------------

    private static void ScanOrphanRunEntries(List<ResidualItem> results, HashSet<string> seen)
    {
        var runKeys = new (RegistryHive hive, string sub)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\RunOnce"),
            (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run"),
            (RegistryHive.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\RunOnce")
        };

        foreach (var (hive, sub) in runKeys)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(sub);
                if (key is null) continue;

                foreach (var valueName in key.GetValueNames())
                {
                    if (string.IsNullOrEmpty(valueName)) continue;
                    var command = key.GetValue(valueName) as string;
                    if (string.IsNullOrWhiteSpace(command)) continue;

                    var expanded = Environment.ExpandEnvironmentVariables(command!);
                    var (exe, _) = UninstallService.ParseCommandLine(expanded);
                    if (!IsMissingLocalTarget(exe)) continue;

                    AddValue(results, seen, hive, sub, valueName,
                        $"自启动项「{valueName}」指向已删除文件：{exe}");
                }
            }
            catch { }
        }
    }

    // ---------------- Judgment and Construction Aids ----------------

    /// <summary>Does the target correspond to a path on the local disk of this machine that no longer exists? Network/NAS paths and non-absolute paths are not considered. </summary>
    private static bool IsMissingLocalTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(raw!).Trim().Trim('"'); }
        catch { return false; }
        if (expanded.Length == 0) return false;
        if (!Path.IsPathRooted(expanded)) return false;   // Unqualified/relative paths cannot be verified
        if (FileSystemUtil.IsNetworkPath(expanded)) return false; // Network locations are not identified or cleaned up
        try { return !File.Exists(expanded) && !Directory.Exists(expanded); }
        catch { return false; }
    }

    private static string StripIconIndex(string icon)
    {
        var s = icon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        // Split only when the character following the comma is an index number (to avoid accidentally splitting commas in the path)
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim().TrimStart('-'), out _))
            s = s[..comma];
        return s.Trim().Trim('"');
    }

    private static void AddKey(List<ResidualItem> results, HashSet<string> seen,
        RegistryHive hive, string subPath, string reason)
    {
        var full = $@"{RootName(hive)}\{subPath}";
        if (!seen.Add("RK:" + full)) return;
        results.Add(new ResidualItem
        {
            Type = ResidualType.RegistryKey,
            Path = full,
            MatchReason = reason,
            Source = ResidualSource.Orphan
        });
    }

    private static void AddValue(List<ResidualItem> results, HashSet<string> seen,
        RegistryHive hive, string subPath, string valueName, string reason)
    {
        var full = $@"{RootName(hive)}\{subPath}";
        if (!seen.Add($"RV:{full}|{valueName}")) return;
        results.Add(new ResidualItem
        {
            Type = ResidualType.RegistryKey,
            Path = full,
            RegistryValueName = valueName,
            MatchReason = reason,
            Source = ResidualSource.Orphan
        });
    }

    private static string RootName(RegistryHive hive)
        => hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";

    // ---------------- Shortcut Analysis (WScript.Shell Late Binding) -----------------

    private static object? CreateWshShell()
    {
        try
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            return t is null ? null : Activator.CreateInstance(t);
        }
        catch { return null; }
    }

    private static string? ResolveShortcutTarget(object? shell, string lnkPath)
    {
        if (shell is null) return null;
        object? sc = null;
        try
        {
            dynamic sh = shell;
            sc = sh.CreateShortcut(lnkPath);
            string target = ((dynamic)sc!).TargetPath;
            return target;
        }
        catch { return null; }
        finally
        {
            // Each shortcut is an independent COM object; release them one by one to prevent RCW accumulation.
            if (sc is not null)
            {
                try { Marshal.FinalReleaseComObject(sc); } catch { }
            }
        }
    }

    // ---------------- ⑤ Isolated Residual Folders ----------------

    // The folders under "Program Files" are part of Windows itself or shared directories and are not considered isolated remnants.
    private static readonly HashSet<string> FolderWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "commonfiles","internetexplorer","windowsdefender",
        "windowsdefenderadvancedthreatprotection","windowsmediaplayer","windowsphotoviewer",
        "windowsmail","windowssidebar","windowsapps","modifiablewindowsapps","windowspowershell",
        "powershell","dotnet","microsoftnet","microsoftsdks","msbuild","referenceassemblies",
        "iisexpress","windowskits","microsoft","microsoftedge","microsoftedgeupdate",
        "microsoftonedrive","onedrive","microsoftupdatehealthtools","uninstallinformation",
        "microsoftvisualstudio","microsoftofficedesktop","windowsappsdk","desktopappinstaller",
        "applicationverifier","msecache","microsoftshared","msxml","windowsnt"
    };

    // Publisher/generic terms used for exclusion during matching to prevent false matches between folder names and product terms
    private static readonly HashSet<string> IndexStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc","llc","ltd","co","corp","corporation","company","gmbh","limited","software",
        "technologies","technology","systems","solutions","group","team","studio","studios",
        "the","and","for","common","files","program","windows","microsoft","x86","x64","win"
    };

    /// <summary>
    /// Scan the top-level folders in Program Files / Program Files (x86) that "do not belong to any installed programs,"
    /// Treat as a suspected isolated remnant (unchecked by default; requires user confirmation). Use a lenient matching strategy to reduce false positives.
    /// </summary>
    private static void ScanOrphanProgramFolders(List<ResidualItem> results, HashSet<string> seen)
    {
        var index = BuildInstalledIndex();

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r)).Distinct();

        foreach (var root in roots)
        {
            List<string> dirs;
            try { dirs = Directory.EnumerateDirectories(root!, "*", TopEnum).ToList(); }
            catch { continue; }

            foreach (var dir in dirs)
            {
                var leafCompact = Compact(Path.GetFileName(dir));
                if (leafCompact.Length == 0) continue;
                // Directories with the "Windows*" or "Microsoft*" prefix are almost exclusively system or Microsoft components and are not considered isolated third-party remnants.
                if (leafCompact.StartsWith("windows", StringComparison.Ordinal)
                    || leafCompact.StartsWith("microsoft", StringComparison.Ordinal)) continue;
                if (FolderWhitelist.Contains(leafCompact)) continue;
                if (index.BelongsToInstalled(dir, leafCompact)) continue;

                var full = Path.GetFullPath(dir).TrimEnd('\\');
                if (!seen.Add("D:" + full)) continue;
                results.Add(new ResidualItem
                {
                    Type = ResidualType.Folder,
                    Path = full,
                    SizeBytes = FileSystemUtil.DirectorySize(full),
                    MatchReason = "疑似孤立残留：不属于任何已安装程序（请确认后再删）",
                    Confidence = ResidualConfidence.Low,
                    Risk = ResidualRisk.Caution,
                    Source = ResidualSource.Orphan,
                    CanAutoSelect = false // High risk of false positives; unchecked by default
                });
            }
        }
    }

    /// <summary> Compiles the "identifying characteristics" of currently installed programs to determine whether a particular folder still belongs to an installed program. </summary>
    private static InstalledIndex BuildInstalledIndex()
    {
        var idx = new InstalledIndex();
        var pfRoots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        }.Where(r => !string.IsNullOrEmpty(r)).Select(r => r!.TrimEnd('\\').ToLowerInvariant()).ToArray();

        try
        {
            foreach (var p in new ProgramScanner().Scan())
            {
                if (!string.IsNullOrWhiteSpace(p.InstallLocation))
                {
                    try { idx.InstallDirs.Add(Path.GetFullPath(p.InstallLocation!).TrimEnd('\\').ToLowerInvariant()); }
                    catch { }
                }
                // Key Point: For many programs, the `InstallLocation` is empty, or the folder name is in Pinyin (e.g., Huorong) and does not match the Chinese name.
                // Determine the directory where the EXE file is located (usually within the actual installation directory) by parsing DisplayIcon and UninstallString, and update the index accordingly.
                AddExeDir(idx, pfRoots, p.DisplayIcon, isCommand: false);
                AddExeDir(idx, pfRoots, p.UninstallString, isCommand: true);
                AddExeDir(idx, pfRoots, p.QuietUninstallString, isCommand: true);

                foreach (var t in IndexTokens(p.DisplayName)) idx.Tokens.Add(t);
                foreach (var t in IndexTokens(p.Publisher)) idx.Tokens.Add(t);
            }
        }
        catch { /* If the list of installed items cannot be retrieved, return an empty index (this rarely results in orphaned items and is the safer option). */ }
        return idx;
    }

    /// <summary>Parse the exe directory from the icon/uninstall command, and add it to the set of installed directories only if it is located under "Program Files." </summary>
    private static void AddExeDir(InstalledIndex idx, string[] pfRoots, string? raw, bool isCommand)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        string path = isCommand ? UninstallService.ParseCommandLine(raw!).exe : StripIconIndex(raw!);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(path).Trim().Trim('"');
            if (!Path.IsPathRooted(expanded)) return;
            var dir = Path.GetDirectoryName(Path.GetFullPath(expanded));
            if (string.IsNullOrEmpty(dir)) return;
            var dl = dir.TrimEnd('\\').ToLowerInvariant();
            foreach (var root in pfRoots)
                if (dl.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    idx.InstallDirs.Add(dl);
                    break;
                }
        }
        catch { /* Ignore invalid paths */ }
    }

    private static IEnumerable<string> IndexTokens(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) yield break;
        foreach (var raw in text.Split(new[] { ' ', '-', '_', '.', ',', '(', ')', '\t', '/', '\\' },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var t = new string(raw.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
            if (t.Length >= 4 && !IndexStopwords.Contains(t))
                yield return t;
        }
    }

    private static string Compact(string s)
        => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    /// <summary>Installed Program Identity Index: Installation Directory + Product/Publisher Keywords. </summary>
    private sealed class InstalledIndex
    {
        public HashSet<string> InstallDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Determines whether a folder still belongs to an installed program (using a lenient criteria; it is better to not classify it as orphaned). </summary>
        public bool BelongsToInstalled(string dir, string leafCompact)
        {
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(); }
            catch { return true; } // If parsing fails, do not take any risks; treat it as "belonging" and do not delete it.

            // This directory itself, along with its parent and child directories, is the installation directory for a specific installed program.
            foreach (var d in InstallDirs)
            {
                if (full == d
                    || full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
                    || d.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // The directory name is a subset of the product/publisher keywords for a certain installed program
            foreach (var t in Tokens)
            {
                if (t.Length >= 4 && (leafCompact.Contains(t) || t.Contains(leafCompact)))
                    return true;
            }
            return false;
        }
    }
}

