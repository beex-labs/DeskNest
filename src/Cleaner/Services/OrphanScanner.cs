using System.IO;
using System.Runtime.InteropServices;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;
using Microsoft.Win32;

namespace BeeXCleaner.Services;

/// <summary>
/// 遗留扫描（全系统）：查找“软件已卸载但仍残留”的记录：
/// ① 失效快捷方式（目标文件已删除）
/// ② 死卸载项（注册表 Uninstall 项，但安装目录/程序文件已不存在）
/// ③ 孤儿 App Paths（注册的可执行文件已删除）
/// ④ 指向已删除文件的自启动项（Run / RunOnce）
/// ⑤ 孤立残留文件夹（Program Files 下不属于任何已安装程序的遗留目录）
/// 前四类以“被引用的本地文件确实不存在”为判据；第五类比对已安装程序清单 + 系统白名单，
/// 默认不勾选交用户确认。网络/NAS 位置一律忽略。
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
    /// 遗留扫描。<paramref name="deep"/>=true 时附加扩展扫描（计划任务/服务/PATH/防火墙/文件关联，
    /// 均以“被引用文件已不存在”为判据，默认不勾选）。
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

    /// <summary>清理（复用共享强制清理器，可选清理会话/结束进程）。</summary>
    public ResidualCleanResult Clean(IEnumerable<ResidualItem> items, bool secureErase = false,
        CleanupSession? session = null, bool killProcesses = false)
        => ResidualCleaner.Clean(items, secureErase, session, killProcesses);

    // ---------------- ① 失效快捷方式 ----------------

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
                    if (string.IsNullOrWhiteSpace(target)) continue; // 无文件目标（URL/商店项）跳过
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
            // 显式释放 WScript.Shell RCW，避免大量 .lnk 扫描后 COM 对象堆积至 GC
            if (shell is not null)
            {
                try { Marshal.FinalReleaseComObject(shell); } catch { }
            }
        }
    }

    // ---------------- ② 死卸载项 ----------------

    private static void ScanDeadUninstallEntries(List<ResidualItem> results, HashSet<string> seen)
    {
        var locations = new (RegistryHive hive, RegistryView view, string sub)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, UninstallBase),
            // 32 位项用 64 位视图 + 显式 WOW6432Node 路径（与本类其它扫描一致）；
            // 若用 Registry32 视图叠加 WOW 路径会双重重定向到不存在的键，导致 32 位死卸载项永远扫不到。
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
                    catch { /* 单项忽略 */ }
                }
            }
            catch { /* 无权限或不存在 */ }
        }
    }

    // ---------------- ③ 孤儿 App Paths ----------------

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

    // ---------------- ④ 失效自启动项 ----------------

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

    // ---------------- 判定与构造辅助 ----------------

    /// <summary>目标是否为“本机本地磁盘上、但已不存在”的路径。网络/NAS 与非绝对路径不判定。</summary>
    private static bool IsMissingLocalTarget(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return false;
        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(raw!).Trim().Trim('"'); }
        catch { return false; }
        if (expanded.Length == 0) return false;
        if (!Path.IsPathRooted(expanded)) return false;   // 裸名/相对路径无法核验
        if (FileSystemUtil.IsNetworkPath(expanded)) return false; // 网络位置不判定、不清理
        try { return !File.Exists(expanded) && !Directory.Exists(expanded); }
        catch { return false; }
    }

    private static string StripIconIndex(string icon)
    {
        var s = icon.Trim().Trim('"');
        var comma = s.LastIndexOf(',');
        // 仅当逗号后是索引数字时才切分（避免误伤路径中的逗号）
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

    // ---------------- 快捷方式解析（WScript.Shell 后期绑定）----------------

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
            // 每个 shortcut 均为独立 COM 对象，逐个释放避免 RCW 堆积
            if (sc is not null)
            {
                try { Marshal.FinalReleaseComObject(sc); } catch { }
            }
        }
    }

    // ---------------- ⑤ 孤立残留文件夹 ----------------

    // Program Files 下这些是 Windows 自身/共享目录，不视为孤立残留
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

    // 匹配时用于剔除的发行商/通用词，避免把文件夹名与产品词误配
    private static readonly HashSet<string> IndexStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "inc","llc","ltd","co","corp","corporation","company","gmbh","limited","software",
        "technologies","technology","systems","solutions","group","team","studio","studios",
        "the","and","for","common","files","program","windows","microsoft","x86","x64","win"
    };

    /// <summary>
    /// 扫描 Program Files / Program Files (x86) 下“不属于任何已安装程序”的顶层文件夹，
    /// 视为疑似孤立残留（默认不勾选，交用户确认）。匹配从宽以减少误判。
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
                // Windows* / Microsoft* 前缀目录几乎都是系统或微软组件，不作为第三方孤立残留
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
                    CanAutoSelect = false // 高误报风险，默认不勾选
                });
            }
        }
    }

    /// <summary>汇总当前已安装程序的“身份特征”，用于判断某文件夹是否仍属于某已安装程序。</summary>
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
                // 关键：很多程序 InstallLocation 为空、或文件夹名是拼音(如火绒 Huorong)与中文名不匹配。
                // 从 DisplayIcon / UninstallString 解析出 exe 所在目录（通常就在真实安装目录内）补充索引。
                AddExeDir(idx, pfRoots, p.DisplayIcon, isCommand: false);
                AddExeDir(idx, pfRoots, p.UninstallString, isCommand: true);
                AddExeDir(idx, pfRoots, p.QuietUninstallString, isCommand: true);

                foreach (var t in IndexTokens(p.DisplayName)) idx.Tokens.Add(t);
                foreach (var t in IndexTokens(p.Publisher)) idx.Tokens.Add(t);
            }
        }
        catch { /* 取不到已安装清单时，返回空索引（此时几乎不产生孤立项，偏安全） */ }
        return idx;
    }

    /// <summary>从图标/卸载命令里解析 exe 目录，仅当其位于 Program Files 之下时补入已安装目录集合。</summary>
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
        catch { /* 忽略无效路径 */ }
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

    /// <summary>已安装程序身份索引：安装目录 + 产品/发行商特征词。</summary>
    private sealed class InstalledIndex
    {
        public HashSet<string> InstallDirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Tokens { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>判断文件夹是否仍属于某个已安装程序（从宽判定，宁可不当作孤立）。</summary>
        public bool BelongsToInstalled(string dir, string leafCompact)
        {
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\').ToLowerInvariant(); }
            catch { return true; } // 解析失败则不冒险，视为“属于”不删

            // 该目录本身/父/子是某已安装程序的安装目录
            foreach (var d in InstallDirs)
            {
                if (full == d
                    || full.StartsWith(d + "\\", StringComparison.OrdinalIgnoreCase)
                    || d.StartsWith(full + "\\", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 目录名与某已安装程序的产品/发行商特征词相互包含
            foreach (var t in Tokens)
            {
                if (t.Length >= 4 && (leafCompact.Contains(t) || t.Contains(leafCompact)))
                    return true;
            }
            return false;
        }
    }
}

