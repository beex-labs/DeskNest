using System.IO;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// 应用数据目录解析：备份与日志的根目录。
/// 优先 %ProgramData%\BeeXCleaner（应用已 requireAdministrator，可写），
/// 失败回退 %LOCALAPPDATA%\BeeXCleaner，再退到临时目录，确保总能落盘。
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "BeeXCleaner";

    private static readonly Lazy<string> _root = new(ResolveRoot);

    /// <summary>数据根目录（已确保存在）。</summary>
    public static string Root => _root.Value;

    /// <summary>注册表备份根目录 Backups\。</summary>
    public static string BackupsRoot => EnsureDir(Path.Combine(Root, "Backups"));

    /// <summary>日志根目录 Logs\。</summary>
    public static string LogsRoot => EnsureDir(Path.Combine(Root, "Logs"));

    private static string ResolveRoot()
    {
        // 首选统一 BeeX 根目录下的 Cleaner 子目录（由设置页统一控制位置）
        try
        {
            var unified = BeeX.DeskNest.BeeXPaths.CleanerDir;
            Directory.CreateDirectory(unified);
            return unified;
        }
        catch { /* 统一根不可用时回退旧链 */ }
        foreach (var special in new[]
        {
            Environment.SpecialFolder.CommonApplicationData, // %ProgramData%
            Environment.SpecialFolder.LocalApplicationData   // %LOCALAPPDATA%
        })
        {
            try
            {
                var baseDir = Environment.GetFolderPath(special);
                if (string.IsNullOrEmpty(baseDir)) continue;
                var dir = Path.Combine(baseDir, AppFolderName);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { /* 尝试下一个候选 */ }
        }

        // 最后兜底：临时目录（几乎总可写）
        var tmp = Path.Combine(Path.GetTempPath(), AppFolderName);
        try { Directory.CreateDirectory(tmp); } catch { /* 忽略 */ }
        return tmp;
    }

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* 忽略 */ }
        return dir;
    }
}
