using System.IO;
using System.Runtime.InteropServices;

namespace BeeXCleaner.Infrastructure;

/// <summary>删除结果：已删除 / 已安排重启后删除（被占用或受保护）/ 失败。</summary>
public enum DeleteResult { Removed, ScheduledReboot, Failed }

/// <summary>
/// 文件系统相关工具：安全目录测量、网络路径识别、健壮强制删除。
/// </summary>
public static class FileSystemUtil
{
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    // 递归枚举时跳过无权限目录与重解析点(junction/软链接)，避免抛异常或死循环
    private static readonly EnumerationOptions RecurseSafe = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary>递归安全计算目录大小（字节）。无权限/重解析点自动跳过。</summary>
    public static long DirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", RecurseSafe))
            {
                try { total += new FileInfo(f).Length; }
                catch { /* 忽略无法访问的文件 */ }
            }
        }
        catch { /* 忽略 */ }
        return total;
    }

    /// <summary>
    /// 健壮强制删除目录：逐文件尽力删除（能删的立即删），删不掉的（被占用/受保护）
    /// 安排在系统重启后由 SYSTEM 删除；目录自底向上删除。做到最大程度零残留。
    /// secureErase=true 时，删除前先用随机字节覆盖文件内容并写盘，使数据不可恢复。
    /// killProcesses=true 时，遇到被占用项会结束占用它的非系统进程后重试；默认为 false，
    /// 只关闭文件句柄，删不掉则安排重启后删除，避免默认静默杀进程导致用户数据丢失。
    /// </summary>
    public static DeleteResult ForceDeleteDirectory(string path, bool secureErase = false, bool killProcesses = false)
    {
        if (!Directory.Exists(path)) return DeleteResult.Removed;

        var scheduled = false;

        // 1) 先逐个删除所有文件（避免“一个删不掉就整体失败”）
        List<string> files;
        try { files = Directory.EnumerateFiles(path, "*", RecurseSafe).ToList(); }
        catch { files = new List<string>(); }
        foreach (var f in files)
        {
            if (DeleteFileRobust(f, secureErase, killProcesses) == DeleteResult.ScheduledReboot) scheduled = true;
        }

        // 2) 目录自底向上删除（先深后浅），最后删根目录
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(path, "*", RecurseSafe).ToList(); }
        catch { dirs = new List<string>(); }
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length)); // 越深越靠前
        dirs.Add(path.TrimEnd('\\', '/'));
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            if (DeleteDirRobust(d, killProcesses) == DeleteResult.ScheduledReboot) scheduled = true;
        }

        if (!Directory.Exists(path)) return DeleteResult.Removed;
        return scheduled ? DeleteResult.ScheduledReboot : DeleteResult.Failed;
    }

    /// <summary>健壮强制删除单个文件（可选安全擦除 → 清属性 → 直接删 → 关句柄 →〔可选〕结束进程 → 安排重启后删）。</summary>
    public static DeleteResult ForceDeleteFile(string path, bool secureErase = false, bool killProcesses = false)
    {
        if (!File.Exists(path)) return DeleteResult.Removed;
        return DeleteFileRobust(path, secureErase, killProcesses);
    }

    private static DeleteResult DeleteFileRobust(string file, bool secureErase, bool killProcesses)
    {
        if (TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed;

        // ① 关闭其它进程持有的文件句柄后重试（不结束进程）
        try { if (FileUnlocker.TryUnlock(file) && TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed; }
        catch { }

        // ② 仅在显式允许时结束占用进程后重试（运行中的程序把 EXE/DLL 作为映像加载时，仅关句柄不足以释放）
        if (killProcesses)
        {
            try { if (FileUnlocker.KillLockers(file) > 0 && TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed; }
            catch { }
        }

        // ③ 兜底：安排系统重启后删除
        try { if (MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT)) return DeleteResult.ScheduledReboot; }
        catch { }
        return DeleteResult.Failed;
    }

    private static bool TryDeleteFileOnce(string file, bool secureErase)
    {
        try
        {
            ClearBlockingAttributes(file);
            if (secureErase) OverwriteFileContents(file); // 覆盖字节后再删，防恢复
            File.Delete(file);
            return true;
        }
        catch { return false; }
    }

    /// <summary>用随机字节覆盖文件全部内容并强制写盘，使删除后数据不可被恢复工具还原。</summary>
    public static void OverwriteFileContents(string path)
    {
        long len;
        try { len = new FileInfo(path).Length; }
        catch { return; }
        if (len <= 0) return;

        // WriteThrough 绕过系统缓存，确保随机字节真正落到磁盘对应簇上
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None,
            1 << 20, FileOptions.WriteThrough);
        var buffer = new byte[1 << 20]; // 1MB
        long remaining = len;
        while (remaining > 0)
        {
            Random.Shared.NextBytes(buffer);
            var chunk = (int)Math.Min(buffer.Length, remaining);
            fs.Write(buffer, 0, chunk);
            remaining -= chunk;
        }
        fs.Flush(flushToDisk: true);
        fs.SetLength(0);   // 截断，抹掉长度信息
        fs.Flush(flushToDisk: true);
    }

    private static DeleteResult DeleteDirRobust(string dir, bool killProcesses)
    {
        // recursive:false —— 目录内文件都已处理；仍非空(有待重启删除的项)则本目录也转重启删除
        if (TryDeleteDirOnce(dir)) return DeleteResult.Removed;

        // ① 目录被占用（如作为某进程的当前工作目录）时，关闭其句柄后重试
        try { if (FileUnlocker.TryUnlock(dir) && TryDeleteDirOnce(dir)) return DeleteResult.Removed; }
        catch { }

        // ② 仅在显式允许时结束占用进程后重试
        if (killProcesses)
        {
            try { if (FileUnlocker.KillLockers(dir) > 0 && TryDeleteDirOnce(dir)) return DeleteResult.Removed; }
            catch { }
        }

        // ③ 兜底：安排系统重启后删除
        try { if (MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT)) return DeleteResult.ScheduledReboot; }
        catch { }
        return DeleteResult.Failed;
    }

    private static bool TryDeleteDirOnce(string dir)
    {
        try { ClearBlockingAttributes(dir); Directory.Delete(dir, recursive: false); return true; }
        catch { return false; }
    }

    /// <summary>清除会阻碍删除的属性（只读/隐藏/系统），保留目录标志。</summary>
    private static void ClearBlockingAttributes(string entry)
    {
        try
        {
            var attrs = File.GetAttributes(entry);
            var cleared = attrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            if (cleared != attrs)
                File.SetAttributes(entry, cleared);
        }
        catch { /* 忽略无法修改属性的项 */ }
    }

    /// <summary>
    /// 判断路径是否位于网络位置（UNC 路径或映射的网络盘 / NAS）。
    /// 用于确保清理仅作用于本机本地磁盘。
    /// </summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        // UNC：\\server\share 或 \\?\UNC\...
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\?\C:\ 这类本地长路径前缀不算网络
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal)
                && !full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        try
        {
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            var drive = new DriveInfo(root);
            return drive.DriveType == DriveType.Network;
        }
        catch
        {
            // 无法识别的卷/断开的映射盘：保守视为网络路径拒绝删除，
            // 与“仅限本机本地磁盘”的安全语义一致（出错时宁可误拦不可误放）。
            return true;
        }
    }
}
