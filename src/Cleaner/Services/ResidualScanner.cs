using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>扫描模式：标准（高置信，默认勾选）/ 深度（更多位置，低置信默认不勾选）。</summary>
public enum ScanMode { Standard, Deep }

/// <summary>
/// 扫描并清理某个程序卸载后残留的文件夹、文件、注册表项、快捷方式，
/// 以达到“彻底清除本机所有记录”的目的。
/// 匹配策略保守，并要求用户逐项确认以避免误删。
/// </summary>
public sealed partial class ResidualScanner
{
    // 视为通用/共享目录或键名，不作为整体匹配目标，避免误删
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
        "Google" // 顶层 Google 键常被多产品共享，仅下钻匹配，不整体删除
    };

    // 健壮枚举选项:跳过无权限目录与重解析点(junction/软链接)，避免抛异常或死循环
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

    /// <summary>扫描指定程序的残留记录。</summary>
    public List<ResidualItem> Scan(InstalledProgram program, ScanMode mode = ScanMode.Standard)
    {
        var results = new List<ResidualItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var publisherCompact = NormalizeCompact(StripPublisher(program.Publisher));
        var publisherTokens = ExtractPublisherTokens(program.Publisher);

        // 产品特征键：剔除发行商词，仅保留“字母词”与“4 位年份”，丢弃非年份的纯数字与点分版本号。
        // 例：Autodesk 3ds Max 2024 → 3dsmax2024；Autodesk Revit 2018.3.3 → revit2018。
        // 保留年份可区分版本（不误删仍安装的其它年版），去掉发行商词可避免命中同厂其它产品。
        var productKey = BuildProductKey(program.DisplayName, publisherTokens);
        // 版本无关基名（进一步去掉年份）：如 3dsmax / revit，用于发现被多版本共享的
        // “版本无关注册表键/目录”（如 HKCU\Software\Autodesk\3dsMax），默认不勾选交用户确认。
        var productBase = BuildProductKey(program.DisplayName, publisherTokens, keepYears: false);

        var ctx = new MatchContext(productKey, productBase, publisherCompact, mode == ScanMode.Deep);

        // 1) 安装目录本身（最强信号）。除注册表 InstallLocation 外，还从 DisplayIcon /
        //    卸载命令解析出 exe 所在目录——大量程序并不写 InstallLocation，靠这些才能定位到
        //    真实安装目录。仍排除“发行商共享根目录”（如 C:\Program Files\Autodesk）避免误删同厂其它产品。
        AddOwnInstallDirs(program, publisherCompact, results, seen);

        // 2) 文件系统常见位置。“我的文档”下的同名目录几乎总装着用户产出内容
        //   （录像/存档等），不是程序残留的可靠信号，一律降为低置信且不默认勾选。
        var docsRoot = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        foreach (var root in GetFileRoots())
            ScanFileRoot(root, ctx, results, seen,
                isUserDocs: !string.IsNullOrEmpty(docsRoot)
                            && string.Equals(root, docsRoot, StringComparison.OrdinalIgnoreCase));

        // 3) 注册表
        ScanRegistryRoot(RegistryHive.CurrentUser, @"SOFTWARE", ctx, results, seen);
        ScanRegistryRoot(RegistryHive.LocalMachine, @"SOFTWARE", ctx, results, seen);
        ScanRegistryRoot(RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node", ctx, results, seen);

        // 3b) App Paths（可执行文件路径注册）
        ScanAppPaths(ctx, results, seen);

        // 4) 快捷方式（开始菜单 + 桌面）
        foreach (var sc in GetShortcutRoots())
            ScanShortcuts(sc, ctx, results, seen);

        return results
            .OrderBy(r => r.Type)
            .ThenBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>执行清理（删除用户勾选的项）。委托给共享强制清理器（可选清理会话/结束进程）。</summary>
    public ResidualCleanResult Clean(IEnumerable<ResidualItem> items, bool secureErase = false,
        CleanupSession? session = null, bool killProcesses = false)
        => ResidualCleaner.Clean(items, secureErase, session, killProcesses);

    // ---------------- 安装目录定位（多信号） ----------------

    /// <summary>
    /// 收集并加入该程序“自己的安装目录”。除注册表 InstallLocation 外，还从 DisplayIcon /
    /// UninstallString / QuietUninstallString 解析出 exe 所在目录（大量程序不写 InstallLocation）。
    /// 逐一施加安全护栏（网络盘/系统灾难性根目录/厂商共享根目录），并去重子目录后加入。
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
            if (!UninstallService.IsSafeToDelete(full)) continue;     // 网络盘 / 系统灾难性根目录拦截
            if (IsVendorRootFolder(full, publisherCompact)) continue; // 厂商共享根不整删
            dirs.Add(full);
        }

        // 去重：若某目录是列表中另一目录的子目录，则丢弃（保留父目录即可）
        var kept = new List<string>();
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(d => d.Length))
        {
            if (kept.Any(k => d.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase))) continue;
            kept.Add(d);
        }
        foreach (var d in kept)
            AddFolder(results, seen, d, "安装目录");
    }

    /// <summary>从 InstallLocation 与各类命令/图标路径中提取候选安装目录。</summary>
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

    /// <summary>解析（命令行或图标）中的 exe，返回其所在目录；裸名/相对路径返回 null。</summary>
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

            // 卸载器常单独放在 \uninstall 之类子目录，向上取到产品根目录以便完整清除
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

    // 卸载器常见的独立子目录名——命中则向上取产品根
    private static readonly HashSet<string> GenericUninstallerDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "uninstall", "uninst", "uninstaller", "uninstallation", "uninstalldata", "uninst.exe"
    };

    /// <summary>去掉 DisplayIcon 末尾的图标索引（如 "app.exe,0" → "app.exe"）。</summary>
    private static string StripIconIndex(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return string.Empty;
        var s = icon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        if (comma > 1 && int.TryParse(s[(comma + 1)..].Trim().TrimStart('-'), out _))
            s = s[..comma];
        return s.Trim().Trim('"');
    }

    // ---------------- 文件系统扫描 ----------------

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

            // 目录名匹配发行商 → 下钻一层匹配产品（如 Publisher\Product）
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
            catch { /* 忽略无法访问的文件 */ }
        }
        return total;
    }

    /// <summary>递归安全枚举文件（跳过无权限目录与重解析点），在 try 内物化避免惰性异常。</summary>
    private static List<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, RecurseSafe).ToList(); }
        catch { return new List<string>(); }
    }

    /// <summary>安全枚举顶层子目录。</summary>
    private static List<string> SafeEnumerateDirectories(string root)
    {
        try { return Directory.EnumerateDirectories(root, "*", TopSafe).ToList(); }
        catch { return new List<string>(); }
    }

    // ---------------- 注册表扫描 ----------------

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
                    // 共享顶层键：仅在其为发行商时下钻，不整体删除
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
        catch { /* 忽略 */ }
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

    /// <summary>扫描 App Paths（可执行文件注册项），匹配产品名的整键作为残留。</summary>
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

    // ---------------- 快捷方式扫描 ----------------

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

    // ---------------- 文本归一化 ----------------

    private static string NormalizeCompact(string? s)
        => string.IsNullOrEmpty(s)
            ? string.Empty
            : new string(s.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    private static string StripName(string name)
    {
        // 去掉版本号、括号内容、常见后缀
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

    /// <summary>提取发行商特征词（小写），用于从产品匹配中排除厂商名。</summary>
    private static HashSet<string> ExtractPublisherTokens(string? publisher)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in SplitTokens(StripPublisher(publisher)))
            if (t.Length >= 2)
                set.Add(t.ToLowerInvariant());
        return set;
    }

    /// <summary>
    /// 构建产品特征键：去括注、剔除发行商词，仅保留“含字母的词”与（可选）“4 位年份”，
    /// 丢弃非年份的纯数字与点分版本号（如 20.00 / 2018.3.3 的小版本段）。
    /// keepYears=false 时进一步去掉年份，得到版本无关基名（如 3dsmax / revit）。
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
    /// 判断某路径是否为“发行商共享根目录”（其叶子名等于发行商名，或为 Common/Shared 等共享目录）。
    /// 这类目录下通常并存多个同厂产品，不能作为单个产品的安装目录整体删除。
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

    // 短名称软件白名单：名称过短（2-3 字符）但为知名软件，仅允许“精确相等”匹配（更严格，避免误伤）。
    private static readonly HashSet<string> ShortNameWhitelist = new(StringComparer.OrdinalIgnoreCase)
    {
        "qq","git","vlc","tim","wps","obs","gimp","npp"
    };

    /// <summary>是否包含 CJK 统一表意文字（用于放宽中文短名的最小匹配长度）。</summary>
    private static bool HasCjk(string s)
    {
        foreach (var c in s)
            if (c >= '\u4e00' && c <= '\u9fff') return true;
        return false;
    }

    /// <summary>由用户手动指定目录构造残留项（低置信，默认不勾选）。</summary>
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

    /// <summary>由用户手动指定注册表完整路径构造残留项（低置信，默认不勾选）。</summary>
    public static ResidualItem CreateManualRegistry(string fullPath)
    {
        // 与 CreateManualFolder 同等归一化：去尾部反斜杠/斜杠方向/连续反斜杠，
        // 否则同一键可被重复添加，删除时还会解析出空键名、备份文件名漂移。
        var normalized = fullPath.Trim().Replace('/', '\\').TrimEnd('\\');
        while (normalized.Contains(@"\\"))
            normalized = normalized.Replace(@"\\", @"\");
        // 根级/系统关键位置不允许作为可删除项构造（整键删除会损坏系统或大量软件注册信息）。
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

    /// <summary>禁止手动添加/整键删除的注册表根级路径（删除会损坏系统或抹掉大量软件注册信息）。</summary>
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
    /// 判断某注册表完整路径是否为“受保护的根级位置”（禁止手动添加/整键删除）。
    /// 归一化大小写、斜杠方向、尾部反斜杠与连续反斜杠后与阻止清单比对；空路径视为受保护。
    /// 除精确清单外，HKLM\SYSTEM 子树额外按深度阈值拦截：CurrentControlSet / ControlSet00x /
    /// 其下的 Services 等浅层键整删会直接破坏系统启动配置，仅放行“具体服务键”及更深层级。
    /// </summary>
    public static bool IsProtectedRegistryRoot(string? fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return true;
        var normalized = fullPath.Trim().Replace('/', '\\').TrimEnd('\\');
        while (normalized.Contains(@"\\"))
            normalized = normalized.Replace(@"\\", @"\");
        if (ProtectedRegistryRoots.Contains(normalized)) return true;

        // HKLM\SYSTEM 下深度 < 3 的键（如 CurrentControlSet、CurrentControlSet\Services）一律拒绝；
        // 深度 3 即具体服务键（...\CurrentControlSet\Services\某服务）仍允许，服务清理依赖此级别。
        const string systemPrefix = @"HKEY_LOCAL_MACHINE\SYSTEM\";
        if (normalized.StartsWith(systemPrefix, StringComparison.OrdinalIgnoreCase)
            && normalized[systemPrefix.Length..].Split('\\').Length < 3)
            return true;

        return false;
    }

    /// <summary>匹配上下文与判定逻辑。</summary>
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
            // 含中文时放宽最小匹配长度到 2（中文名通常很短）；纯 ASCII 仍要求 >= 4 以降低误报。
            _minLen = HasCjk(productKey) ? 2 : 4;
            _shortWhitelisted = productKey.Length is >= 2 and <= 3 && ShortNameWhitelist.Contains(productKey);
        }

        public (bool matched, string reason, ResidualConfidence confidence, bool autoSelect) IsMatch(string rawName)
        {
            var n = NormalizeCompact(rawName);
            if (n.Length == 0) return (false, "", ResidualConfidence.Low, false);

            // 高置信：带版本前缀匹配（版本专属残留），默认勾选。
            // 残留目录/键/快捷方式通常以产品名开头，用前缀可避免把“XXX for Revit”误当成 Revit。
            if (_productKey.Length >= _minLen && n.StartsWith(_productKey, StringComparison.Ordinal))
                return (true, "产品名匹配", ResidualConfidence.High, true);

            // 中置信：短名称白名单“精确相等”（如 Git/VLC/QQ），默认勾选但更保守。
            if (_shortWhitelisted && n == _productKey)
                return (true, "短名称精确匹配", ResidualConfidence.Medium, true);

            // 低置信：版本无关产品基名“精确相等”（如 3dsMax 键，可能被多版本共享）。
            // 仅当产品名确实含版本(基名≠带版本键)时启用，默认不勾选，交用户确认，避免误删其它版本。
            if (_productBase.Length >= _minLen && _productBase != _productKey && n == _productBase)
                return (true, "版本无关键（可能与其它版本共享，请确认）", ResidualConfidence.Low, false);

            // 深度扫描：名称“包含”产品特征（低置信，默认不勾选，交用户确认）。
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
