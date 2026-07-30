using System.IO;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;

namespace BeeXCleaner.Services;

/// <summary>可擦除磁盘信息。</summary>
public sealed record WipeDriveInfo(string Root, string? Label, string TypeText, long TotalBytes, long FreeBytes)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Root : $"{Root} ({Label})";
    public string FreeText => InstalledProgram.FormatSize(FreeBytes);
    public string TotalText => InstalledProgram.FormatSize(TotalBytes);
}

/// <summary>擦除进度。</summary>
public sealed class WipeProgress
{
    public double Fraction { get; init; }
    public long Written { get; init; }
    public long Target { get; init; }
}

/// <summary>擦除结果。</summary>
public sealed record WipeResult(bool Completed, bool Cancelled, long WrittenBytes, string Message);

/// <summary>
/// 可用空间深度擦除：用随机数据覆盖磁盘的“可用空间”，从而摧毁此前已删除（逻辑删除、
/// 可被恢复工具还原）文件的数据字节，使其无法再被恢复。仅限本机本地磁盘。
/// 会临时占满可用空间（保留安全余量），完成后自动清除填充文件。
/// </summary>
public sealed class FreeSpaceWiper
{
    private const long MarginBytes = 1L << 30;          // 非系统盘保留 1GB
    private const long SystemMarginBytes = 5L << 30;    // 系统盘保留 5GB：页面文件/VSS/更新在磁盘近满时会失败
    private const long FileChunk = 1L << 30;   // 每个填充文件最大 1GB（兼容各类文件系统）
    private const string WipeDirPrefix = "_BeeXCleaner_Wipe_";

    private const int ERROR_DISK_FULL = unchecked((int)0x80070070);
    private const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);

    // 正在擦除中的填充目录：启动期后台回收与新开始的擦除存在竞态，回收时必须跳过活动目录
    private static readonly HashSet<string> ActiveDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveDirsLock = new();

    /// <summary>该盘的安全余量：系统盘预留更大空间，避免擦除期间系统不稳定。</summary>
    public static long GetMarginBytes(string driveRoot)
    {
        try
        {
            var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(sysRoot)
                && string.Equals(Path.GetPathRoot(driveRoot), sysRoot, StringComparison.OrdinalIgnoreCase))
                return SystemMarginBytes;
        }
        catch { /* 无法判定时按非系统盘处理 */ }
        return MarginBytes;
    }

    /// <summary>
    /// 回收历史残留的填充目录：擦除中途进程被杀/断电时，盘根的 _BeeXCleaner_Wipe_* 会把磁盘
    /// 保持在近满状态且普通用户难以定位。应在应用启动时后台调用。返回清除的目录数。
    /// </summary>
    public static int CleanupLeftoverFillDirs()
    {
        var cleaned = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                foreach (var dir in Directory.EnumerateDirectories(d.RootDirectory.FullName, WipeDirPrefix + "*"))
                {
                    // 跳过本进程正在使用的填充目录，避免并发删除回退擦除进度
                    lock (ActiveDirsLock) { if (ActiveDirs.Contains(dir)) continue; }
                    try { Directory.Delete(dir, recursive: true); cleaned++; }
                    catch (Exception ex) { AppLogger.Warn($"回收残留填充目录失败: {dir}", ex); }
                }
            }
            catch { /* 跳过异常驱动器 */ }
        }
        if (cleaned > 0) AppLogger.Info($"已回收 {cleaned} 个残留的擦除填充目录");
        return cleaned;
    }

    /// <summary>列出可擦除的本机磁盘（本地固定盘 / 可移动盘；排除网络盘/NAS/光驱）。</summary>
    public List<WipeDriveInfo> GetWipeableDrives()
    {
        var list = new List<WipeDriveInfo>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                list.Add(new WipeDriveInfo(
                    d.RootDirectory.FullName,
                    string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel,
                    d.DriveType == DriveType.Removable ? "可移动盘" : "本地磁盘",
                    d.TotalSize, d.AvailableFreeSpace));
            }
            catch { /* 跳过异常驱动器 */ }
        }
        return list;
    }

    /// <summary>擦除指定驱动器的可用空间（带进度与取消）。</summary>
    public async Task<WipeResult> WipeAsync(string driveRoot, IProgress<WipeProgress>? progress, CancellationToken ct)
    {
        DriveInfo di;
        try { di = new DriveInfo(driveRoot); }
        catch (Exception ex) { return new WipeResult(false, false, 0, ex.Message); }

        if (!di.IsReady) return new WipeResult(false, false, 0, "驱动器不可用。");
        if (di.DriveType == DriveType.Network || FileSystemUtil.IsNetworkPath(driveRoot))
            return new WipeResult(false, false, 0, "网络盘 / NAS 不允许擦除，仅限本机本地磁盘。");

        var margin = GetMarginBytes(driveRoot);
        var initialFree = di.AvailableFreeSpace;
        var target = Math.Max(0, initialFree - margin);
        if (target <= 0) return new WipeResult(true, false, 0, "可用空间过小，无需擦除。");

        var dir = Path.Combine(driveRoot, WipeDirPrefix + Guid.NewGuid().ToString("N"));
        var buffer = new byte[4 << 20]; // 4MB 缓冲
        long written = 0;
        var cancelled = false;
        string? ioError = null;
        lock (ActiveDirsLock) { ActiveDirs.Add(dir); }

        try
        {
            Directory.CreateDirectory(dir);
            var idx = 0;
            var lastFree = initialFree;
            var stall = 0;

            while (!ct.IsCancellationRequested)
            {
                var free = di.AvailableFreeSpace;
                if (free <= margin) break;

                var fileTarget = Math.Min(FileChunk, free - margin);
                if (fileTarget <= 0) break;

                var file = Path.Combine(dir, $"w{idx++}.tmp");
                try
                {
                    // WriteThrough 绕过缓存，确保随机字节真正落盘覆盖可用簇
                    using var fs = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                        buffer.Length, FileOptions.WriteThrough);
                    long fileWritten = 0;
                    while (fileWritten < fileTarget)
                    {
                        ct.ThrowIfCancellationRequested();
                        Random.Shared.NextBytes(buffer);
                        var chunk = (int)Math.Min(buffer.Length, fileTarget - fileWritten);
                        await fs.WriteAsync(buffer.AsMemory(0, chunk), ct).ConfigureAwait(false);
                        fileWritten += chunk;
                        written += chunk;
                        progress?.Report(new WipeProgress
                        {
                            Written = written,
                            Target = target,
                            Fraction = target > 0 ? Math.Min(1.0, (double)written / target) : 1.0
                        });
                    }
                    fs.Flush(flushToDisk: true);
                }
                catch (OperationCanceledException) { cancelled = true; break; }
                catch (IOException ex)
                {
                    // 只有“磁盘已满”才是正常完成信号；其它 IO 错误（权限/坏道/目录被删）
                    // 不能据此报告“数据不可恢复”，否则安全承诺失实。
                    if (ex.HResult is not (ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL))
                        ioError = ex.Message;
                    break;
                }

                // 防止可用空间不再下降（配额/其它写入者）导致死循环
                var nowFree = di.AvailableFreeSpace;
                if (nowFree >= lastFree) { if (++stall >= 2) break; } else stall = 0;
                lastFree = nowFree;
            }

            if (ct.IsCancellationRequested) cancelled = true;
        }
        catch (OperationCanceledException) { cancelled = true; }
        catch (Exception ex)
        {
            TryCleanup(dir);
            lock (ActiveDirsLock) { ActiveDirs.Remove(dir); }
            return new WipeResult(false, cancelled, written, ex.Message);
        }

        TryCleanup(dir); // 清除填充文件，恢复可用空间
        lock (ActiveDirsLock) { ActiveDirs.Remove(dir); }

        if (cancelled)
            return new WipeResult(false, true, written, "已取消；填充文件已清理，可用空间已恢复。");
        if (ioError is not null)
            return new WipeResult(false, false, written,
                $"擦除未完成（写入出错：{ioError}）。已写入部分不影响现有文件，但尚未覆盖的可用空间中的已删除数据仍可能被恢复。");
        return new WipeResult(true, false, written,
            "擦除完成：可用空间已被随机数据覆盖，此前删除的文件不可再被恢复。");
    }

    private static void TryCleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* 尽力清理；残留填充文件用户可手动删 */ }
    }
}
