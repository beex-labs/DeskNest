using System.IO;
using BeeXCleaner.Infrastructure;
using BeeXCleaner.Models;

namespace BeeXCleaner.Services;

/// <summary>Information on erasable disks. </summary>
public sealed record WipeDriveInfo(string Root, string? Label, string TypeText, long TotalBytes, long FreeBytes)
{
    public string Display => string.IsNullOrWhiteSpace(Label) ? Root : $"{Root} ({Label})";
    public string FreeText => InstalledProgram.FormatSize(FreeBytes);
    public string TotalText => InstalledProgram.FormatSize(TotalBytes);
}

/// <summary>Erase progress. </summary>
public sealed class WipeProgress
{
    public double Fraction { get; init; }
    public long Written { get; init; }
    public long Target { get; init; }
}

/// <summary>Erasure results. </summary>
public sealed record WipeResult(bool Completed, bool Cancelled, long WrittenBytes, string Message);

/// <summary>
/// Deep Erasure of Free Space: Overwriting the disk’s “free space” with random data to destroy data that has been previously deleted (logically deleted,
/// (Data bytes in files that can be recovered using data recovery tools, rendering them unrecoverable. Applies only to local disks on this computer.)
/// It will temporarily fill all available space (to ensure a safety margin), and the padding files will be automatically deleted upon completion.
/// </summary>
public sealed class FreeSpaceWiper
{
    private const long MarginBytes = 1L << 30;          // Reserve 1 GB on a non-system drive
    private const long SystemMarginBytes = 5L << 30;    // Reserve 5 GB on the system drive: Page file/VSS/updates may fail when the disk is nearly full
    private const long FileChunk = 1L << 30;   // Maximum file size of 1 GB per file (compatible with all file systems)
    private const string WipeDirPrefix = "_BeeXCleaner_Wipe_";

    private const int ERROR_DISK_FULL = unchecked((int)0x80070070);
    private const int ERROR_HANDLE_DISK_FULL = unchecked((int)0x80070027);

    // Directory being erased: There is a race condition between background reclamation during the startup phase and a newly initiated erase operation; active directories must be skipped during reclamation.
    private static readonly HashSet<string> ActiveDirs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ActiveDirsLock = new();

    /// <summary>Safety margin for this drive: Reserve more space on the system drive to prevent system instability during the wipe process.</summary>
    public static long GetMarginBytes(string driveRoot)
    {
        try
        {
            var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (!string.IsNullOrEmpty(sysRoot)
                && string.Equals(Path.GetPathRoot(driveRoot), sysRoot, StringComparison.OrdinalIgnoreCase))
                return SystemMarginBytes;
        }
        catch { /* If it cannot be determined, treat it as a non-system drive. */ }
        return MarginBytes;
    }

    /// <summary>
    /// Recovering historical residual fill directories: When a wipe process is terminated or interrupted due to a power outage, the _BeeXCleaner_Wipe_* process in the root directory will wipe the disk
    /// Keep the device nearly full and make it difficult for regular users to locate. This should be called in the background when the app launches. Returns the number of directories cleared.
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
                    // Skip the fill directory currently being used by this process to prevent concurrent deletions from rolling back the erasure progress
                    lock (ActiveDirsLock) { if (ActiveDirs.Contains(dir)) continue; }
                    try { Directory.Delete(dir, recursive: true); cleaned++; }
                    catch (Exception ex) { AppLogger.Warn($"回收残留填充目录失败: {dir}", ex); }
                }
            }
            catch { /* Skip Faulty Drives */ }
        }
        if (cleaned > 0) AppLogger.Info($"已回收 {cleaned} 个残留的擦除填充目录");
        return cleaned;
    }

    /// <summary>Lists erasable local disks (local fixed disks / removable disks; excludes network drives, NAS, and optical drives). </summary>
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
            catch { /* Skip Faulty Drives */ }
        }
        return list;
    }

    /// <summary>Erase free space on a specified drive (with progress bar and cancel option). </summary>
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
        var buffer = new byte[4 << 20]; // 4 MB buffer
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
                    // WriteThrough bypasses the cache to ensure that random bytes are actually written to disk, overwriting available clusters.
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
                    // Only "Disk Full" is a normal completion signal; other I/O errors (permissions, bad sectors, deleted directory)
                    // This report should not be used as grounds to conclude that “the data is unrecoverable”; otherwise, the security assurance would be false.
                    if (ex.HResult is not (ERROR_DISK_FULL or ERROR_HANDLE_DISK_FULL))
                        ioError = ex.Message;
                    break;
                }

                // Prevent an infinite loop caused by available space no longer decreasing (quotas/other writers)
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

        TryCleanup(dir); // Clear the padding files to recover free space
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
        catch { /* Do your best to clean up; users can manually delete any remaining placeholder files. */ }
    }
}
