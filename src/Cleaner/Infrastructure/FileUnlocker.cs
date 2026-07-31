using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// Releases a lock held on a file or folder so it can be deleted immediately instead of on reboot. Steps:
/// (1) use Restart Manager to find the processes locking the path;
/// (2) enumerate system handles (NtQuerySystemInformation) and close the file handles those processes hold
///     to the path via DuplicateHandle(DUPLICATE_CLOSE_SOURCE) (without killing the processes);
/// (3) always skip critical system processes (System/csrss/lsass, etc.) to avoid a crash.
/// Fully user-mode; requires administrator rights (the app manifest already sets requireAdministrator).
/// </summary>
public static class FileUnlocker
{
    /// <summary>Attempts to release the lock on a path (file or directory). Returns whether at least one lock handle has been closed. </summary>
    public static bool TryUnlock(string path)
    {
        try
        {
            var full = Path.GetFullPath(path).TrimEnd('\\', '/');
            var ntPath = ToNtPath(full);
            if (ntPath is null) return false;

            var pids = GetLockingProcesses(full);
            if (pids.Count == 0) return false;

            return CloseHandlesTo(ntPath, pids) > 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Terminate non-critical processes that are using this path. Used in situations where "closing the handle still fails to delete the file"—running programs are using
    /// When an EXE/DLL is loaded as an image, simply closing the file handle is not sufficient to free the memory; the process must be terminated. Returns the number of terminated processes.
    /// Critical system processes and shell processes such as explorer and dwm are all skipped; this process itself is skipped.
    /// </summary>
    public static int KillLockers(string path)
    {
        var killed = 0;
        List<int> pids;
        try { pids = GetLockingProcesses(Path.GetFullPath(path).TrimEnd('\\', '/')); }
        catch { return 0; }

        foreach (var pid in pids)
        {
            if (pid == Environment.ProcessId || IsCriticalPid(pid)) continue;
            try
            {
                using var p = Process.GetProcessById(pid);
                if (CriticalNames.Contains(p.ProcessName) || NoKillNames.Contains(p.ProcessName)) continue;
                p.Kill(entireProcessTree: true);
                p.WaitForExit(3000);
                killed++;
            }
            catch { /* The process has exited or cannot be terminated; ignore */ }
        }
        return killed;
    }

    // Shell/Session Daemon: It does not terminate even if it is using files (terminating it would disrupt the desktop, input method, etc.); instead, it is left to be handled by a system restart as a fallback.
    private static readonly HashSet<string> NoKillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "sihost", "ctfmon", "fontdrvhost", "runtimebroker", "taskhostw"
    };

    // ==================== Restart Manager: Locating Processes That Are Taking Up Resources ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct RM_UNIQUE_PROCESS
    {
        public int dwProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
    }

    private enum RM_APP_TYPE
    {
        RmUnknownApp = 0, RmMainWindow = 1, RmOtherWindow = 2, RmService = 3,
        RmExplorer = 4, RmConsole = 5, RmCritical = 1000
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RM_PROCESS_INFO
    {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public RM_APP_TYPE ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint pSessionHandle);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames,
        uint nApplications, RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded,
        ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, out uint lpdwRebootReasons);

    private const int ERROR_MORE_DATA = 234;

    /// <summary>Use Restart Manager to find the PID of the process locking that path.</summary>
    public static List<int> GetLockingProcesses(string path)
    {
        var pids = new List<int>();
        if (RmStartSession(out var session, 0, Guid.NewGuid().ToString()) != 0) return pids;
        try
        {
            string[] resources = { path };
            if (RmRegisterResources(session, 1, resources, 0, null, 0, null) != 0) return pids;

            uint needed = 0, count = 0, reason = 0;
            var res = RmGetList(session, out needed, ref count, null, out reason);
            if (res == ERROR_MORE_DATA && needed > 0)
            {
                var arr = new RM_PROCESS_INFO[needed];
                count = needed;
                if (RmGetList(session, out needed, ref count, arr, out reason) == 0)
                {
                    for (var i = 0; i < count; i++)
                    {
                        // Skip system processes that Restart Manager deems critical
                        if (arr[i].ApplicationType == RM_APP_TYPE.RmCritical) continue;
                        pids.Add(arr[i].Process.dwProcessId);
                    }
                }
            }
        }
        finally { RmEndSession(session); }
        return pids;
    }

    // ==================== Handle Enumeration + Forced Closure ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_ENTRY
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    private const int SystemExtendedHandleInformation = 0x40;
    private const int ObjectNameInformation = 1;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_CLOSE_SOURCE = 0x1;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int SystemInformationClass,
        IntPtr SystemInformation, int SystemInformationLength, out int ReturnLength);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryObject(IntPtr Handle, int ObjectInformationClass,
        IntPtr ObjectInformation, int ObjectInformationLength, out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr hSourceProcess, IntPtr hSource,
        IntPtr hTargetProcess, out IntPtr lpTarget, uint access, bool inherit, uint options);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    /// <summary>Closes all file handles in pids that point to ntPath (or any of its subpaths). Returns the number of handles closed. </summary>
    private static int CloseHandlesTo(string ntPath, List<int> pids)
    {
        var targetPids = new HashSet<int>(pids);
        var closed = 0;
        var current = GetCurrentProcess();
        var procCache = new Dictionary<int, IntPtr>();

        foreach (var h in EnumerateHandles())
        {
            var pid = (int)h.UniqueProcessId;
            if (!targetPids.Contains(pid)) continue;
            if (IsCriticalPid(pid)) continue;

            if (!procCache.TryGetValue(pid, out var src))
            {
                src = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                procCache[pid] = src;
            }
            if (src == IntPtr.Zero) continue;

            // Copy to this process to query its name
            if (!DuplicateHandle(src, h.HandleValue, current, out var dup, 0, false, DUPLICATE_SAME_ACCESS))
                continue;

            var (completed, name) = GetHandleNameWithTimeout(dup, 150);
            // When a query times out, the background thread may still be blocked on `NtQueryObject(dup)`: In this case, closing `dup` will cause the handle value to
            // If reused, a thread that has resumed execution may encounter an invalid handle. Therefore, it is closed only when the query completes successfully (timeout handles are extremely rare,
            // (It is better to let a handle leak until the process exits than to accidentally manipulate an unrelated handle.)
            if (completed) CloseHandle(dup);
            if (string.IsNullOrEmpty(name)) continue;

            var match = name.Equals(ntPath, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(ntPath + "\\", StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            // Key step: Copy using DUPLICATE_CLOSE_SOURCE, which is equivalent to closing the handle in the source process → release the handle
            if (DuplicateHandle(src, h.HandleValue, IntPtr.Zero, out _, 0, false, DUPLICATE_CLOSE_SOURCE))
                closed++;
        }

        foreach (var p in procCache.Values) if (p != IntPtr.Zero) CloseHandle(p);
        return closed;
    }

    private static IEnumerable<SYSTEM_HANDLE_ENTRY> EnumerateHandles()
    {
        var len = 0x100000;
        var buffer = Marshal.AllocHGlobal(len);
        try
        {
            uint status;
            while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, len, out var need))
                   == STATUS_INFO_LENGTH_MISMATCH)
            {
                // First release and zero out, then reallocate: If AllocHGlobal throws an exception (when the handle table is large, double the value of len),
                // A dangling pointer will be freed a second time by `FreeHGlobal` within the `finally` block, causing heap corruption.
                Marshal.FreeHGlobal(buffer);
                buffer = IntPtr.Zero;
                len = need > 0 ? need + 0x10000 : len * 2;
                buffer = Marshal.AllocHGlobal(len);
            }
            if (status != 0) yield break;

            // SYSTEM_HANDLE_INFORMATION_EX: [nint NumberOfHandles][nint Reserved][entries...]
            var count = Marshal.ReadIntPtr(buffer).ToInt64();
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_ENTRY>();
            var start = buffer + IntPtr.Size * 2;
            for (long i = 0; i < count; i++)
            {
                yield return Marshal.PtrToStructure<SYSTEM_HANDLE_ENTRY>(start + (nint)(i * entrySize));
            }
        }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }

    private static string? GetHandleName(IntPtr handle)
    {
        var size = 0x1000;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var status = NtQueryObject(handle, ObjectNameInformation, buf, size, out var ret);
            if (status != 0) return null;
            var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
            if (us.Buffer == IntPtr.Zero || us.Length == 0) return null;
            return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
        }
        catch { return null; }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Query handle names with a timeout: Certain synchronous handles (such as named pipes) can cause NtQueryObject to hang; if a timeout occurs, the operation is abandoned.
    /// A return value of `completed=false` indicates that a background thread may still be using the handle, and the caller should not close it. </summary>
    private static (bool completed, string? name) GetHandleNameWithTimeout(IntPtr handle, int timeoutMs)
    {
        string? name = null;
        var t = new System.Threading.Thread(() =>
        {
            try { name = GetHandleName(handle); }
            catch { /* Ignore */ }
        })
        { IsBackground = true };
        t.Start();
        return t.Join(timeoutMs) ? (true, name) : (false, null);
    }

    /// <summary>Convert Win32 paths to kernel object paths (\Device\HarddiskVolumeN\...) to match handle names. </summary>
    private static string? ToNtPath(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null; // Local Market Only
            var drive = root.TrimEnd('\\'); // "C:"
            var sb = new StringBuilder(1024);
            if (QueryDosDevice(drive, sb, 1024) == 0) return null;
            return sb + fullPath[drive.Length..];
        }
        catch { return null; }
    }

    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "MemCompression", "Memory Compression"
    };

    private static bool IsCriticalPid(int pid)
    {
        if (pid is 0 or 4) return true;
        try
        {
            using var p = Process.GetProcessById(pid);
            return CriticalNames.Contains(p.ProcessName);
        }
        catch { return false; }
    }
}
