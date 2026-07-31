using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>Scan Mode: Standard (High Confidence, checked by default) / Deep (More Locations, Low Confidence, unchecked by default). </summary>
public enum ScanMode { Standard, Deep }

/// <summary>
/// Scan and clean up folders, files, registry entries, and shortcuts left behind after uninstalling a program,
/// To "completely erase all records from this computer."
/// The matching strategy is conservative and requires users to confirm each item individually to prevent accidental deletion.
/// </summary>
public sealed partial class ResidualScanner
{
    // Treat as a general/shared directory or key name; do not treat as a whole for matching purposes to avoid accidental deletion
    private static readonly HashSet<string> Stopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc","llc","ltd","co","corp","corporation","company","gmbh","limited",
        "software","technologies","technology","systems","solutions","group",
        "team","studio","studios","the","and","for","version","x86","x64","win",
        "windows","microsoft","common","program","files","application","app",
        "data","update","updater","installer","setup","tools","plugin","plugins",
        "alpha","beta","rc","build","edition","pro","professional","enterprise",
        "home","standard","free","plus","lite","full","x32","amd64","arm64"
    };

    private static readonly HashSet<string> RegistryBlockKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft","Windows","Classes","Wow6432Node","Policies","Clients",
        "RegisteredApplications","Intel","ODBC","Khronos","Nvidia","AMD",
        "Google" // The top-level "Google" key is often shared across multiple products; only drill-down matches are affected—it is not deleted entirely.
    };

    // Robust enumeration options: Skip directories without access permissions and junction points (junction points/symbolic links) to avoid throwing exceptions or entering an infinite loop
    private static readonly EnumerationOptions RecurseSafe = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    private static readonly EnumerationOptions TopSafe = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>Scans for residual records from specified programs.</summary>
    public List<ResidualItem> Scan(InstalledProgram program, ScanMode mode = ScanMode.Standard)
    {
        var results = new List<ResidualItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var publisherCompact = NormalizeCompact(StripPublisher(program.Publisher));
        var publisherTokens = ExtractPublisherTokens(program.Publisher);

        // Product Key Features: Excludes publisher terms, retaining only "alphabetic terms" and "4-digit years"; discards non-year-related numeric values and decimal version numbers.
        // Example: Autodesk 3ds Max 2024 → 3dsmax2024; Autodesk Revit 2018.3.3 → revit2018.
        // Including the year helps distinguish between versions (so you don’t accidentally delete other versions of the same software that are still installed), and removing the publisher’s name helps avoid matching other products from the same company.
        var productKey = BuildProductKey(program.DisplayName, publisherTokens);
        // Version-independent base names (with the year removed): such as 3dsmax / revit, used to identify names shared across multiple versions
        // "Version has no registry keys/directories" (e.g., HKCU\Software\Autodesk\3dsMax); by default, this option is unchecked and requires user confirmation.
        var productBase = BuildProductKey(program.DisplayName, publisherTokens, keepYears: false);

        var ctx = new MatchContext(productKey, productBase, publisherCompact, mode == ScanMode.Deep);

        // 1) The installation directory itself (strongest signal). In addition to the InstallLocation registry key, it also retrieves information from DisplayIcon /
        //    The uninstallation command identifies the directory where the EXE file is located—since many programs do not specify the `InstallLocation`, this is the only way to locate them.
        //    Actual installation directory. The "publisher's shared root directory" (such as C:\Program Files\Autodesk) is still excluded to prevent accidental deletion of other products from the same vendor.
        AddOwnInstallDirs(program, publisherCompact, results, seen);

        // 2) Common file system locations. The folder with the same name under “My Documents” almost always contains user-generated content.
        //   (Recordings, archives, etc.)—which are not reliable signals of program remnants—are all downgraded to "low confidence" and are not checked by default.
        var docsRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var root in GetFileRoots())
            ScanFileRoot(root, ctx, results, seen,
                isUserDocs: !string.IsNullOrEmpty(docsRoot)
                            && string.Equals(root, docsRoot, StringComparison.OrdinalIgnoreCase));

        // 3) Registry
        ScanRegistryRoot(RegistryHive.CurrentUser, @"SOFTWARE", ctx, results, seen);
        ScanRegistryRoot(RegistryHive.LocalMachine, @"SOFTWARE", ctx, results, seen);
        ScanRegistryRoot(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node", ctx, results, seen);

        // 3b) App Paths (Registration of Executable File Paths)
        ScanAppPaths(ctx, results, seen);

        // 4) Shortcuts (Start Menu + Desktop)
        foreach (var sc in GetShortcutRoots())
            ScanShortcuts(sc, ctx, results, seen);

        return results
            .OrderBy(r => r.Type)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Perform cleanup (delete the items selected by the user). Delegate to the Shared Force Cleanup Tool (optional: clean up sessions/terminate processes).</summary>
    public ResidualCleanResult Clean(IEnumerable<ResidualItem> items, bool secureErase = false,
        CleanupSession? session = null, bool killProcesses = false)
        => ResidualCleaner.Clean(items, secureErase, session, killProcesses);

    // ---------------- Locating the Installation Directory (Multiple Signals) ----------------

    /// <summary>
    /// Collect and add them to the program's "own installation directory." In addition to the InstallLocation registry key, also from DisplayIcon /
    /// UninstallString / QuietUninstallString determine the directory where the EXE file is located (many programs do not specify the InstallLocation).
    /// Apply security barriers one by one (network drive/system root directory/vendor shared root directory), and add them after removing duplicate subdirectories.
    /// </summary>
    private static void AddOwnInstallDirs(InstalledProgram program, string publisherCompact,
        List<ResidualItem> results, HashSet<string> seen)
    {
        var dirs = new List<string>();
        foreach (var raw in CollectInstallDirCandidates(program))
        {
            string full;
            try { full = Path.GetFullPath(raw).TrimEnd('\\', '/'); }
            catch { continue; }
            if (full.Length < 4 || !Directory.Exists(full)) continue;
            if (!UninstallService.IsSafeToDelete(full)) continue;     // Network Drive / System-Wide Root Directory Interception
            if (IsVendorRootFolder(full, publisherCompact)) continue; // Do not delete the shared root directory for vendors
            dirs.Add(full);
        }

        // Remove duplicates: If a directory is a subdirectory of another directory in the list, discard it (only keep the parent directory).
        var kept = new List<string>();
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length))
        {
            if (kept.Any(k => d.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(d);
        }
        foreach (var d in kept)
            AddFolder(results, seen, d, "安装目录");
    }

    /// <summary>Extract candidate installation directories from the `InstallLocation` and the paths of various commands and icons.</summary>
    private static IEnumerable<string> CollectInstallDirCandidates(InstalledProgram p)
    {
        if (!string.IsNullOrWhiteSpace(p.InstallLocation))
            yield return p.InstallLocation!;

        var fromIcon = ExeDir(StripIconIndex(p.DisplayIcon), isCommand: false);
        if (fromIcon is not null) yield return fromIcon;

        var fromUninstall = ExeDir(p.UninstallString, isCommand: true);
        if (fromUninstall is not null) yield return fromUninstall;

        var fromQuiet = ExeDir(p.QuietUninstallString, isCommand: true);
        if (fromQuiet is not null) yield return fromQuiet;
    }

    /// <summary>Parses the "exe" in a command line or icon and returns the directory where it is located; returns null for bare names or relative paths. </summary>
    private static string? ExeDir(string? raw, bool isCommand)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var exe = isCommand ? UninstallService.ParseCommandLine(raw!).exe : raw!;
        if (string.IsNullOrWhiteSpace(exe)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(exe).Trim().Trim('"');
            if (!Path.IsPathRooted(expanded)) return null;
            var dir = Path.GetDirectoryName(Path.GetFullPath(expanded));
            if (string.IsNullOrEmpty(dir)) return null;

            // Uninstallers are often placed in a separate subdirectory, such as \uninstall, and the path is traced back to the product's root directory to ensure complete removal.
            var leaf = Path.GetFileName(dir);
            if (GenericUninstallerDirs.Contains(leaf))
            {
                var parent = Path.GetDirectoryName(dir);
                if (!string.IsNullOrEmpty(parent)) dir = parent;
            }
            return dir;
        }
        catch { return null; }
    }

    // Common standalone subdirectory names for uninstallers—if a match is found, go up to the product root
    private static readonly HashSet<string> GenericUninstallerDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall", "uninst", "uninstaller", "uninstallation", "uninstalldata", "uninst.exe"
    };

    /// <summary>Remove the icon index from the end of DisplayIcon (e.g., "app.exe,0" → "app.exe").</summary>
    private static string StripIconIndex(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return string.Empty;
        var s = icon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim().TrimStart('-'), out _))
            s = s[..comma];
        return s.Trim().Trim('"');
    }

    // ---------------- File System Scan ----------------

    private static IEnumerable<string> GetFileRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow"),
            Path.GetTempPath()
        };
        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))!.Distinct()!;
    }

    private void ScanFileRoot(string root, MatchContext ctx, List<ResidualItem> results, HashSet<string> seen,
        bool isUserDocs = false)
    {
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var name = Path.GetFileName(dir);
            var m = ctx.IsMatch(name);
            if (m.matched)
            {
                if (isUserDocs)
                    AddFolder(results, seen, dir, $"{m.reason}（文档目录，可能包含个人文件，请确认）",
                        autoSelect: false, ResidualConfidence.Low);
                else
                    AddFolder(results, seen, dir, m.reason, m.autoSelect, m.confidence);
                continue;
            }

            // Match by catalog name to publisher → Drill down one level to match products (e.g., Publisher\Product)
            if (ctx.IsPublisher(name))
                ScanPublisherChildren(dir, ctx, results, seen, isUserDocs);
        }
    }

    private void ScanPublisherChildren(string publisherDir, MatchContext ctx, List<ResidualItem> results,
        HashSet<string> seen, bool isUserDocs = false)
    {
        foreach (var dir in SafeEnumerateDirectories(publisherDir))
        {
            var name = Path.GetFileName(dir);
            var m = ctx.IsMatch(name);
            if (!m.matched) continue;
            if (isUserDocs)
                AddFolder(results, seen, dir, $"{m.reason}（文档目录·发行商目录内，可能包含个人文件，请确认）",
                    autoSelect: false, ResidualConfidence.Low);
            else
                AddFolder(results, seen, dir, $"{m.reason}（发行商目录内）", m.autoSelect, m.confidence);
        }
    }

    private static void AddFolder(List<ResidualItem> results, HashSet<string> seen, string path, string reason,
        bool autoSelect = true, ResidualConfidence confidence = ResidualConfidence.High)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        if (!seen.Add("D:" + full)) return;
        results.Add(new ResidualItem
        {
            Type = ResidualType.Folder,
            Path = full,
            SizeBytes = SafeDirectorySize(full),
            MatchReason = reason,
            Confidence = confidence,
            Source = confidence == ResidualConfidence.Low ? ResidualSource.DeepScan : ResidualSource.InstallDir,
            CanAutoSelect = autoSelect
        });
    }

    private static long SafeDirectorySize(string dir)
    {
        long total = 0;
        foreach (var f in SafeEnumerateFiles(dir, "*"))
        {
            try { total += new FileInfo(f).Length; }
            catch { /* Ignore files that cannot be accessed */ }
        }
        return total;
    }

    /// <summary> Recursively and safely enumerate files (skipping directories without permissions and re-parsing points), and materialize them within the `try` block to avoid lazy exceptions. </summary>
    private static List<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, RecurseSafe).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>Safely enumerate top-level subdirectories.</summary>
    private static List<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", TopSafe).ToList(); }
        catch { return new List<string>(); }
    }

    // ---------------- Registry Scan ----------------

    private void ScanRegistryRoot(RegistryHive hive, string subPath, MatchContext ctx,
        List<ResidualItem> results, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(subPath);
            if (root is null) return;

            foreach (var name in root.GetSubKeyNames())
            {
                if (RegistryBlockKeys.Contains(name))
                {
                    // Share top-level keys: Drill down only when they are publishers; do not delete them entirely
                    if (ctx.IsPublisher(name))
                        ScanRegistryChildren(hive, $@"{subPath}\{name}", ctx, results, seen);
                    continue;
                }

                var m = ctx.IsMatch(name);
                if (m.matched)
                {
                    AddRegistry(results, seen, hive, $@"{subPath}\{name}", m.reason, m.autoSelect, m.confidence);
                }
                else if (ctx.IsPublisher(name))
                {
                    ScanRegistryChildren(hive, $@"{subPath}\{name}", ctx, results, seen);
                }
            }
        }
        catch (Exception ex) { AppLogger.Warn($"扫描注册表位置失败: {subPath}", ex); }
    }

    private void ScanRegistryChildren(RegistryHive hive, string subPath, MatchContext ctx,
        List<ResidualItem> results, HashSet<string> seen)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(subPath);
            if (key is null) return;

            foreach (var name in key.GetSubKeyNames())
            {
                var m = ctx.IsMatch(name);
                if (m.matched)
                    AddRegistry(results, seen, hive, $@"{subPath}\{name}", $"{m.reason}（发行商键内）", m.autoSelect, m.confidence);
            }
        }
        catch { /* Ignore */ }
    }

    private static void AddRegistry(List<ResidualItem> results, HashSet<string> seen,
        RegistryHive hive, string subPath, string reason, bool autoSelect = true,
        ResidualConfidence confidence = ResidualConfidence.High)
    {
        var rootName = hive == RegistryHive.CurrentUser ? "HKEY_CURRENT_USER" : "HKEY_LOCAL_MACHINE";
        var full = $@"{rootName}\{subPath}";
        if (!seen.Add("R:" + full)) return;
        results.Add(new ResidualItem
        {
            Type = ResidualType.RegistryKey,
            Path = full,
            SizeBytes = 0,
            MatchReason = reason,
            Confidence = confidence,
            Source = ResidualSource.Registry,
            CanAutoSelect = autoSelect
        });
    }

    /// <summary>Scan App Paths (executable file entries) and match the entire key containing the product name as a residue. </summary>
    private void ScanAppPaths(MatchContext ctx, List<ResidualItem> results, HashSet<string> seen)
    {
        var locations = new (RegistryHive hive, string path)[]
        {
            (RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\App Paths"),
            (RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths")
        };

        foreach (var (hive, path) in locations)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
                using var root = baseKey.OpenSubKey(path);
                if (root is null) continue;

                foreach (var name in root.GetSubKeyNames())
                {
                    var stem = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? name[..^4]
                        : name;
                    var m = ctx.IsMatch(stem);
                    if (m.matched)
                        AddRegistry(results, seen, hive, $@"{path}\{name}", $"{m.reason}（App Paths）", m.autoSelect, m.confidence);
                }
            }
            catch (Exception ex) { AppLogger.Warn($"扫描 App Paths 失败: {path}", ex); }
        }
    }

    // ---------------- Shortcut Scan ----------------

    private static IEnumerable<string> GetShortcutRoots()
    {
        var roots = new List<string?>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)
        };
        return roots.Where(r => !string.IsNullOrEmpty(r) && Directory.Exists(r))!.Distinct()!;
    }

    private void ScanShortcuts(string root, MatchContext ctx, List<ResidualItem> results, HashSet<string> seen)
    {
        foreach (var lnk in SafeEnumerateFiles(root, "*.lnk"))
        {
            var name = Path.GetFileNameWithoutExtension(lnk);
            var m = ctx.IsMatch(name);
            if (!m.matched) continue;
            if (!seen.Add("S:" + lnk)) continue;

            long size = 0;
            try { size = new FileInfo(lnk).Length; } catch { }
            results.Add(new ResidualItem
            {
                Type = ResidualType.Shortcut,
                Path = lnk,
                SizeBytes = size,
                MatchReason = m.reason,
                Confidence = m.confidence,
                Source = ResidualSource.Shortcut,
                CanAutoSelect = m.autoSelect
            });
        }
    }

    // ---------------- Text Normalization ----------------

    private static string NormalizeCompact(string? s)
        => string.IsNullOrEmpty(s)
            ? string.Empty
            : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string StripName(string name)
    {
        // Remove version numbers, content in parentheses, and common suffixes
        name = ParentheticalRegex().Replace(name, " ");
        name = VersionRegex().Replace(name, " ");
        return name.Trim();
    }

    private static string StripPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher)) return string.Empty;
        var p = ParentheticalRegex().Replace(publisher, " ");
        var tokens = SplitTokens(p).Where(t => !Stopwords.Contains(t));
        return string.Join(" ", tokens);
    }

    private static List<string> ExtractTokens(string name)
        => SplitTokens(StripName(name))
            .Select(t => t.ToLowerInvariant())
            .Where(t => t.Length >= 3 && !Stopwords.Contains(t))
            .Distinct()
            .ToList();

    /// <summary>Extract publisher keywords (in lowercase) to exclude manufacturer names from product matches.</summary>
    private static HashSet<string> ExtractPublisherTokens(string? publisher)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in SplitTokens(StripPublisher(publisher)))
            if (t.Length >= 2)
                set.Add(t.ToLowerInvariant());
        return set;
    }

    /// <summary>
    /// Constructing product feature keys: Remove parentheses and exclude publisher terms, retaining only “words containing letters” and (optionally) “4-digit years,”
    /// Discard version numbers consisting solely of numbers without a year and those with a period and a decimal (such as 20.00 or 2018.3.3 for minor version segments).
    /// When `keepYears=false`, the year is further removed to obtain a version-independent base name (such as 3dsmax / revit).
    /// </summary>
    private static string BuildProductKey(string displayName, HashSet<string> publisherTokens, bool keepYears = true)
    {
        var noParen = ParentheticalRegex().Replace(displayName, " ");
        var tokens = SplitTokens(noParen)
            .Select(t => t.ToLowerInvariant())
            .Where(t => !publisherTokens.Contains(t))
            .Where(t => t.Any(char.IsLetter) || (keepYears && IsYear(t)));
        return string.Concat(tokens);
    }

    private static bool IsYear(string t)
        => t.Length == 4 && int.TryParse(t, out var y) && y is >= 1980 and <= 2099;

    /// <summary>
    /// Determine whether a given path is a “publisher shared root directory” (where the leaf name matches the publisher name, or is a shared directory such as Common/Shared).
    /// These directories typically contain multiple products from the same manufacturer and cannot be deleted in their entirety as the installation directory for a single product.
    /// </summary>
    private static bool IsVendorRootFolder(string path, string publisherCompact)
    {
        var leaf = NormalizeCompact(Path.GetFileName(path.TrimEnd('\\', '/')));
        if (leaf.Length == 0) return true;
        if (publisherCompact.Length >= 4 && leaf == publisherCompact) return true;
        return leaf is "common" or "commonfiles" or "shared";
    }

    private static IEnumerable<string> SplitTokens(string s)
        => TokenSplitRegex().Split(s).Where(t => t.Length > 0);

    [GeneratedRegex(@"\(.*?\)")]
    private static partial Regex ParentheticalRegex();

    [GeneratedRegex(@"\bv?\d+(\.\d+)*\b")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex TokenSplitRegex();

    // Short-Name Software Whitelist: Software with very short names (2–3 characters) but that are well-known; only “exact matches” are allowed (a stricter rule to avoid false positives).
    private static readonly HashSet<string> ShortNameWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "qq","git","vlc","tim","wps","obs","gimp","npp"
    };

    /// <summary>Whether CJK Unified Ideographs are included (used to relax the minimum match length for Chinese short names).</summary>
    private static bool HasCjk(string s)
    {
        foreach (var c in s)
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>Manually specify a directory to generate residual items (low confidence; unchecked by default). </summary>
    public static ResidualItem CreateManualFolder(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd('\\');
        return new ResidualItem
        {
            Type = ResidualType.Folder,
            Path = full,
            SizeBytes = SafeDirectorySize(full),
            MatchReason = "手动添加",
            Confidence = ResidualConfidence.Low,
            Source = ResidualSource.Manual,
            CanAutoSelect = false
        };
    }

    /// <summary>Manually specify the full registry path to create a residual entry (low confidence; unchecked by default). </summary>
    public static ResidualItem CreateManualRegistry(string fullPath)
    {
        // Normalization equivalent to CreateManualFolder: Remove trailing backslashes, forward slashes, and consecutive backslashes,
        // Otherwise, the same key could be added multiple times, and when it is deleted, an empty key name might be parsed, causing the backup file name to drift.
        var normalized = fullPath.Trim().Replace('/', '\\').TrimEnd('\\');
        while (normalized.Contains(@"\\"))
            normalized = normalized.Replace(@"\\", @"\");
        // Root-level and system-critical locations must not be configured as deletable items (deleting an entire key would damage the system or corrupt a large amount of software registry information).
        if (IsProtectedRegistryRoot(normalized))
            throw new ArgumentException("该注册表路径为受保护的系统/软件根级位置，禁止作为可删除残留项。");
        return new()
        {
            Type = ResidualType.RegistryKey,
            Path = normalized,
            MatchReason = "手动添加",
            Confidence = ResidualConfidence.Low,
            Source = ResidualSource.Manual,
            CanAutoSelect = false
        };
    }

    /// <summary>Registry root-level paths that must not be manually added or deleted in their entirety (deleting them may damage the system or erase a large amount of software registry information).</summary>
    private static readonly HashSet<string> ProtectedRegistryRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        @"HKEY_LOCAL_MACHINE",
        @"HKEY_CURRENT_USER",
        @"HKEY_LOCAL_MACHINE\SOFTWARE",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Classes",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows NT",
        @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion",
        @"HKEY_LOCAL_MACHINE\SYSTEM",
        @"HKEY_CURRENT_USER\SOFTWARE",
        @"HKEY_CURRENT_USER\SOFTWARE\Classes",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT",
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows NT\CurrentVersion"
    };

    /// <summary>
    /// Determine whether a specific full registry path is a “protected root-level location” (where manual additions or deletion of entire keys are prohibited).
    /// Compare the path after normalizing case, slash direction, trailing backslashes, and consecutive backslashes against the blocklist; empty paths are considered protected.
    /// In addition to the precise list, the HKLM\SYSTEM subtree is further filtered based on depth thresholds: CurrentControlSet / ControlSet00x /
    /// Modifying or deleting shallow-level keys such as "Services" will directly corrupt the system boot configuration; only "specific service keys" and deeper levels are permitted.
    /// </summary>
    public static bool IsProtectedRegistryRoot(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return true;
        var normalized = fullPath.Trim().Replace('/', '\\').TrimEnd('\\');
        while (normalized.Contains(@"\\"))
            normalized = normalized.Replace(@"\\", @"\");
        if (ProtectedRegistryRoots.Contains(normalized)) return true;

        // All keys under HKLM\SYSTEM at a depth of less than 3 (such as CurrentControlSet and CurrentControlSet\Services) are rejected;
        // Level 3 (i.e., specific service keys such as ...\CurrentControlSet\Services\[Service Name] ) is still permitted; service cleanup relies on this level.
        const string systemPrefix = @"HKEY_LOCAL_MACHINE\SYSTEM\";
        if (normalized.StartsWith(systemPrefix, StringComparison.OrdinalIgnoreCase)
            && normalized[systemPrefix.Length..].Split('\\').Length < 3)
            return true;

        return false;
    }

    /// <summary>Matching context and decision logic. </summary>
    private sealed class MatchContext
    {
        private readonly string _productKey;
        private readonly string _productBase;
        private readonly string _publisherCompact;
        private readonly bool _deep;
        private readonly int _minLen;
        private readonly bool _shortWhitelisted;

        public MatchContext(string productKey, string productBase, string publisherCompact, bool deep)
        {
            _productKey = productKey;
            _productBase = productBase;
            _publisherCompact = publisherCompact;
            _deep = deep;
            // When Chinese characters are present, the minimum match length is relaxed to 2 (since Chinese names are typically short); for pure ASCII, the minimum match length remains >= 4 to reduce false positives.
            _minLen = HasCjk(productKey) ? 2 : 4;
            _shortWhitelisted = productKey.Length is >= 2 and <= 3 && ShortNameWhitelist.Contains(productKey);
        }

        public (bool matched, string reason, ResidualConfidence confidence, bool autoSelect) IsMatch(string rawName)
        {
            var n = NormalizeCompact(rawName);
            if (n.Length == 0) return (false, "", ResidualConfidence.Low, false);

            // High Confidence: Matches with version prefixes (version-specific residues); checked by default.
            // Residual folders, keys, and shortcuts typically begin with the product name; using a prefix can prevent "XXX for Revit" from being mistaken for Revit.
            if (_productKey.Length >= _minLen && n.StartsWith(_productKey, StringComparison.Ordinal))
                return (true, "产品名匹配", ResidualConfidence.High, true);

            // Zhongzhixin: Short name whitelist "exact match" (e.g., Git/VLC/QQ); checked by default but more conservative.
            if (_shortWhitelisted && n == _productKey)
                return (true, "短名称精确匹配", ResidualConfidence.Medium, true);

            // Low Confidence: "Exact matches" for version-independent product base names (e.g., the "3dsMax" key, which may be shared across multiple versions).
            // Enable this only when the product name actually includes a version (base name ≠ version-included key). By default, this option is unchecked and requires user confirmation to prevent accidental deletion of other versions.
            if (_productBase.Length >= _minLen && _productBase != _productKey && n == _productBase)
                return (true, "版本无关键（可能与其它版本共享，请确认）", ResidualConfidence.Low, false);

            // Deep Scan: Names that “include” product features (low confidence; unchecked by default; requires user confirmation).
            if (_deep && _productKey.Length >= _minLen && n.Contains(_productKey, StringComparison.Ordinal))
                return (true, "深度扫描·名称包含产品特征（请确认）", ResidualConfidence.Low, false);

            return (false, "", ResidualConfidence.Low, false);
        }

        public bool IsPublisher(string rawName)
        {
            if (_publisherCompact.Length < 4) return false;
            var n = NormalizeCompact(rawName);
            return n == _publisherCompact;
        }
    }
}
