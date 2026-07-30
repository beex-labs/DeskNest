using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// 文件占用解除器：复刻 Unlocker/LockHunter 思路，主动解除文件/文件夹的占用后再删除，
/// 而不是留到重启。步骤：
/// ① 用 Restart Manager 找出锁定该路径的进程；
/// ② 枚举系统句柄（NtQuerySystemInformation），把这些进程持有、指向该路径的文件句柄
///    通过 DuplicateHandle(DUPLICATE_CLOSE_SOURCE) 直接关闭（不杀进程）；
/// ③ 关键系统进程（System/csrss/lsass 等）一律跳过，避免蓝屏。
/// 全部为用户态实现，需管理员权限（本程序清单已 requireAdministrator）。
/// </summary>
public static class FileUnlocker
{
    /// <summary>尝试解除对 path（文件或目录）的占用。返回是否至少关闭了一个占用句柄。</summary>
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
    /// 结束占用该路径的非关键进程。用于“关句柄仍无法删除”的情形——运行中的程序把
    /// EXE/DLL 作为映像加载时，仅关文件句柄不足以释放，必须结束进程。返回结束的进程数。
    /// 关键系统进程与 explorer/dwm 等外壳进程一律跳过；本进程自身跳过。
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
            catch { /* 进程已退出或无法结束，忽略 */ }
        }
        return killed;
    }

    // 外壳/会话基础进程：即便占用文件也不结束（结束会破坏桌面/输入法等），交由重启兜底
    private static readonly HashSet<string> NoKillNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "sihost", "ctfmon", "fontdrvhost", "runtimebroker", "taskhostw"
    };

    // ==================== Restart Manager：定位占用进程 ====================

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

    /// <summary>用 Restart Manager 找出锁定该路径的进程 PID。</summary>
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
                        // 跳过 Restart Manager 判定为关键的系统进程
                        if (arr[i].ApplicationType == RM_APP_TYPE.RmCritical) continue;
                        pids.Add(arr[i].Process.dwProcessId);
                    }
                }
            }
        }
        finally { RmEndSession(session); }
        return pids;
    }

    // ==================== 句柄枚举 + 强制关闭 ====================

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

    /// <summary>关闭 pids 中所有指向 ntPath（或其子路径）的文件句柄。返回关闭数量。</summary>
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

            // 复制到本进程以查询其名称
            if (!DuplicateHandle(src, h.HandleValue, current, out var dup, 0, false, DUPLICATE_SAME_ACCESS))
                continue;

            var (completed, name) = GetHandleNameWithTimeout(dup, 150);
            // 查询超时时后台线程可能仍阻塞在 NtQueryObject(dup) 上：此时关闭 dup 会使句柄值
            // 被复用，苏醒后的线程可能查到无关句柄。故仅在查询正常完成时关闭（超时句柄极少，
            // 泄漏到进程退出好于误操作无关句柄）。
            if (completed) CloseHandle(dup);
            if (string.IsNullOrEmpty(name)) continue;

            var match = name.Equals(ntPath, StringComparison.OrdinalIgnoreCase)
                        || name.StartsWith(ntPath + "\\", StringComparison.OrdinalIgnoreCase);
            if (!match) continue;

            // 关键一步：以 DUPLICATE_CLOSE_SOURCE 复制，等效于在源进程里关闭该句柄 → 解除占用
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
                // 先释放并置零再重新分配：若 AllocHGlobal 抛出（句柄表巨大时 len 翻倍），
                // 悬空指针会在 finally 中被二次 FreeHGlobal，造成堆损坏
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

    /// <summary>带超时地查询句柄名称：个别同步句柄(如命名管道)会令 NtQueryObject 卡死，超时即放弃。
    /// 返回 completed=false 表示后台线程可能仍在使用该句柄，调用方不应关闭它。</summary>
    private static (bool completed, string? name) GetHandleNameWithTimeout(IntPtr handle, int timeoutMs)
    {
        string? name = null;
        var t = new System.Threading.Thread(() =>
        {
            try { name = GetHandleName(handle); }
            catch { /* 忽略 */ }
        })
        { IsBackground = true };
        t.Start();
        return t.Join(timeoutMs) ? (true, name) : (false, null);
    }

    /// <summary>把 Win32 路径转成内核对象路径（\Device\HarddiskVolumeN\...）以匹配句柄名。</summary>
    private static string? ToNtPath(string fullPath)
    {
        try
        {
            var root = Path.GetPathRoot(fullPath);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return null; // 仅本地盘
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
