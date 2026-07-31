using System.IO;
using System.Runtime.InteropServices;

namespace BeeXCleaner.Infrastructure;

/// <summary>Deletion results: Deleted / Scheduled for deletion after reboot (in use or protected) / Failed. </summary>
public enum DeleteResult { Removed, ScheduledReboot, Failed }

/// <summary>
/// File system-related tools: secure directory measurement, network path identification, and robust forced deletion.
/// </summary>
public static class FileSystemUtil
{
    private const int MOVEFILE_DELAY_UNTIL_REBOOT = 0x00000004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string lpExistingFileName, string? lpNewFileName, int dwFlags);

    // When performing recursive enumeration, skip directories without access permissions and junction points (junction/symbolic links) to avoid throwing exceptions or entering an infinite loop.
    private static readonly EnumerationOptions RecurseSafe = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    /// <summary> Recursively and safely calculates directory size (in bytes). Automatically skips directories without permissions or with re-resolved paths. </summary>
    public static long DirectorySize(string path)
    {
        long total = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(path, "*", RecurseSafe))
            {
                try { total += new FileInfo(f).Length; }
                catch { /* Ignore files that cannot be accessed */ }
            }
        }
        catch { /* Ignore */ }
        return total;
    }

    /// <summary>
    /// Robust Forced Directory Deletion: Deletes files one by one as much as possible (deleting immediately those that can be deleted); files that cannot be deleted (in use or protected)
    /// Schedule deletion by SYSTEM after the system restarts; delete directories from the bottom up. Ensure minimal residual data remains.
    /// When `secureErase=true`, the file contents are overwritten with random bytes and written to disk before deletion, making the data unrecoverable.
    /// When `killProcesses=true`, if an occupied resource is encountered, the non-system process occupying it will be terminated before retrying; the default is `false`,
    /// Only close the file handle; if the file cannot be deleted, schedule its deletion after a restart to prevent the default silent process termination from causing user data loss.
    /// </summary>
    public static DeleteResult ForceDeleteDirectory(string path, bool secureErase = false, bool killProcesses = false)
    {
        if (!Directory.Exists(path)) return DeleteResult.Removed;

        var scheduled = false;

        // 1) First, delete all files one by one (to avoid a situation where “if one file can’t be deleted, the entire process fails”).
        List<string> files;
        try { files = Directory.EnumerateFiles(path, "*", RecurseSafe).ToList(); }
        catch { files = new List<string>(); }
        foreach (var f in files)
        {
            if (DeleteFileRobust(f, secureErase, killProcesses) == DeleteResult.ScheduledReboot) scheduled = true;
        }

        // 2) Delete the directory from the bottom up (from deepest to shallowest), and delete the root directory last
        List<string> dirs;
        try { dirs = Directory.EnumerateDirectories(path, "*", RecurseSafe).ToList(); }
        catch { dirs = new List<string>(); }
        dirs.Sort((a, b) => b.Length.CompareTo(a.Length)); // The deeper, the closer to the front
        dirs.Add(path.TrimEnd('\\', '/'));
        foreach (var d in dirs)
        {
            if (!Directory.Exists(d)) continue;
            if (DeleteDirRobust(d, killProcesses) == DeleteResult.ScheduledReboot) scheduled = true;
        }

        if (!Directory.Exists(path)) return DeleteResult.Removed;
        return scheduled ? DeleteResult.ScheduledReboot : DeleteResult.Failed;
    }

    /// <summary>Forcefully delete a single file (optional: secure erase → clear attributes → direct delete → close handle → [optional] terminate process → schedule deletion after reboot).</summary>
    public static DeleteResult ForceDeleteFile(string path, bool secureErase = false, bool killProcesses = false)
    {
        if (!File.Exists(path)) return DeleteResult.Removed;
        return DeleteFileRobust(path, secureErase, killProcesses);
    }

    private static DeleteResult DeleteFileRobust(string file, bool secureErase, bool killProcesses)
    {
        if (TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed;

        // ① Retry after closing the file handles held by other processes (without terminating the processes)
        try { if (FileUnlocker.TryUnlock(file) && TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed; }
        catch { }

        // ② Retry only after terminating the process that is holding the resource, if explicitly permitted (when a running program loads an EXE/DLL as an image, simply closing the handle is not sufficient to release the resource).
        if (killProcesses)
        {
            try { if (FileUnlocker.KillLockers(file) > 0 && TryDeleteFileOnce(file, secureErase)) return DeleteResult.Removed; }
            catch { }
        }

        // ③ Fallback: Schedule deletion after the system restarts
        try { if (MoveFileEx(file, null, MOVEFILE_DELAY_UNTIL_REBOOT)) return DeleteResult.ScheduledReboot; }
        catch { }
        return DeleteResult.Failed;
    }

    private static bool TryDeleteFileOnce(string file, bool secureErase)
    {
        try
        {
            ClearBlockingAttributes(file);
            if (secureErase) OverwriteFileContents(file); // Overwrite the bytes before deleting to prevent recovery
            File.Delete(file);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Overwrites the entire contents of a file with random bytes and forces the data to be written to disk, making it impossible for data recovery tools to restore the data after deletion.</summary>
    public static void OverwriteFileContents(string path)
    {
        long len;
        try { len = new FileInfo(path).Length; }
        catch { return; }
        if (len <= 0) return;

        // WriteThrough bypasses the system cache to ensure that random bytes are actually written to the corresponding clusters on the disk.
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
        fs.SetLength(0);   // Truncate, remove length information
        fs.Flush(flushToDisk: true);
    }

    private static DeleteResult DeleteDirRobust(string dir, bool killProcesses)
    {
        // recursive:false —— All files in the directory have been processed; if the directory is still non-empty (containing items to be deleted upon restart), this directory will also be marked for deletion upon restart
        if (TryDeleteDirOnce(dir)) return DeleteResult.Removed;

        // ① If the directory is in use (e.g., as the current working directory of a process), close its handle and retry.
        try { if (FileUnlocker.TryUnlock(dir) && TryDeleteDirOnce(dir)) return DeleteResult.Removed; }
        catch { }

        // ② Retry only after terminating the occupying process, if explicitly permitted
        if (killProcesses)
        {
            try { if (FileUnlocker.KillLockers(dir) > 0 && TryDeleteDirOnce(dir)) return DeleteResult.Removed; }
            catch { }
        }

        // ③ Fallback: Schedule deletion after the system restarts
        try { if (MoveFileEx(dir, null, MOVEFILE_DELAY_UNTIL_REBOOT)) return DeleteResult.ScheduledReboot; }
        catch { }
        return DeleteResult.Failed;
    }

    private static bool TryDeleteDirOnce(string dir)
    {
        try { ClearBlockingAttributes(dir); Directory.Delete(dir, recursive: false); return true; }
        catch { return false; }
    }

    /// <summary>Clears attributes that prevent deletion (Read-Only/Hidden/System), while retaining the directory flag.</summary>
    private static void ClearBlockingAttributes(string entry)
    {
        try
        {
            var attrs = File.GetAttributes(entry);
            var cleared = attrs & ~(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            if (cleared != attrs)
                File.SetAttributes(entry, cleared);
        }
        catch { /* Ignore items whose properties cannot be modified */ }
    }

    /// <summary>
    /// Determine whether the path is a network location (UNC path or a mapped network drive/NAS).
    /// Used to ensure that the cleanup operation applies only to the local disk on this machine.
    /// </summary>
    public static bool IsNetworkPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        string full;
        try { full = Path.GetFullPath(path); }
        catch { return false; }

        // UNC: \\server\share or \\?\UNC\...
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
        {
            // \\?\C:\ Local long path prefixes like this are not considered network paths
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
            // Unrecognized volume/disconnected mapped drive: For safety, treat this as a network path and do not delete it,
            // Consistent with the security semantics of “local disk on this machine only” (in case of an error, it is better to block by mistake than to allow access by mistake).
            return true;
        }
    }
}
