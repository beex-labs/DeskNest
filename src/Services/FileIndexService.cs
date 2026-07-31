using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BeeX.DeskNest;

/// <summary>單個文件命中結果：名稱、完整路徑、是否目錄與匹配得分（越大越靠前）。</summary>
public readonly record struct FileHit(string Name, string FullPath, bool IsDirectory, int Score);

/// <summary>
/// 單卷純內存文件名索引：FRN →（父目錄 FRN、名稱、是否目錄）。
/// 複刻 Everything 原理的存儲層——路徑不落地，查詢時沿父鏈拼裝；讀寫鎖保證 USN 增量更新與搜索並發安全。
/// 不含任何 Win32 依賴，可直接單元測試。
/// </summary>
public sealed class FileNameIndex
{
    readonly string root;
    readonly Dictionary<ulong, Entry> entries = new(1 << 18);
    readonly ReaderWriterLockSlim gate = new();
    struct Entry { public ulong Parent; public string Name; public bool IsDir; }

    public FileNameIndex(char drive) { root = char.ToUpperInvariant(drive) + @":\"; }

    public int Count { get { gate.EnterReadLock(); try { return entries.Count; } finally { gate.ExitReadLock(); } } }

    public void Set(ulong frn, ulong parent, string name, bool isDir)
    {
        gate.EnterWriteLock();
        try { entries[frn] = new Entry { Parent = parent, Name = name, IsDir = isDir }; }
        finally { gate.ExitWriteLock(); }
    }

    public void Remove(ulong frn)
    {
        gate.EnterWriteLock();
        try { entries.Remove(frn); }
        finally { gate.ExitWriteLock(); }
    }

    public string ResolvePath(ulong frn)
    {
        gate.EnterReadLock();
        try { return ResolveNoLock(frn); }
        finally { gate.ExitReadLock(); }
    }

    string ResolveNoLock(ulong frn)
    {
        // 沿父 FRN 鏈向上拼路徑；深度上限防環（NTFS 正常目錄樹不會超過 64 層）
        var parts = new List<string>(8);
        var current = frn; var depth = 0;
        while (depth++ < 64 && entries.TryGetValue(current, out var e)) { parts.Add(e.Name); current = e.Parent; }
        parts.Reverse();
        return root + string.Join('\\', parts);
    }

    /// <summary>空格分詞 AND 匹配（不區分大小寫），完整命中 &gt; 前綴 &gt; 子串，短名優先；結果追加進 results。</summary>
    public void SearchInto(string query, List<FileHit> results, int limit)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0 || limit <= 0) return;
        var matches = new List<(int Score, ulong Frn)>();
        gate.EnterReadLock();
        try
        {
            foreach (var kv in entries)
            {
                var name = kv.Value.Name;
                var ok = true;
                foreach (var token in tokens)
                    if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
                if (!ok) continue;
                var first = tokens[0];
                var score = name.Equals(first, StringComparison.OrdinalIgnoreCase) ? 300
                    : name.StartsWith(first, StringComparison.OrdinalIgnoreCase) ? 200 : 100;
                score -= Math.Min(60, name.Length);
                matches.Add((score, kv.Key));
            }
            matches.Sort((a, b) => b.Score.CompareTo(a.Score));
            foreach (var (score, frn) in matches.Take(limit))
            {
                var e = entries[frn];
                results.Add(new FileHit(e.Name, ResolveNoLock(frn), e.IsDir, score));
            }
        }
        finally { gate.ExitReadLock(); }
    }
}

/// <summary>
/// 自研全盤文件索引服務（Everything 原理復刻，不依賴 Everything 軟體/SDK）：
/// 每個 NTFS 固定卷直接枚舉 MFT（FSCTL_ENUM_USN_DATA）秒級建立文件名索引，
/// 再由後台線程監聽 USN Journal（FSCTL_READ_USN_JOURNAL）實時增量更新。
/// 打開卷句柄需要管理員權限——主程式 manifest 已設 requireAdministrator。
/// </summary>
public sealed class FileIndexService : IDisposable
{
    readonly List<VolumeIndexer> volumes = [];
    readonly object sync = new();
    bool started, disposed;

    /// <summary>至少一個卷成功打開（未提權/無 NTFS 卷時為 false，調用方應降級提示）。</summary>
    public bool Available { get { lock (sync) return volumes.Count > 0; } }
    /// <summary>全部卷完成首次 MFT 枚舉，可提供完整結果。</summary>
    public bool Ready { get { lock (sync) return started && volumes.Count > 0 && volumes.All(v => v.Ready); } }
    public int TotalCount { get { lock (sync) return volumes.Sum(v => v.Index.Count); } }

    public void Start()
    {
        lock (sync) { if (started || disposed) return; started = true; }
        Task.Run(() =>
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady ||
                        !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                    var indexer = new VolumeIndexer(drive.Name[0]);
                    if (!indexer.Open()) { indexer.Dispose(); continue; }
                    lock (sync) { if (disposed) { indexer.Dispose(); return; } volumes.Add(indexer); }
                    indexer.Start();
                }
                catch { }
            }
        });
    }

    public List<FileHit> Search(string query, int limit)
    {
        List<VolumeIndexer> current;
        lock (sync) current = [.. volumes];
        var hits = new List<FileHit>();
        foreach (var volume in current) volume.Index.SearchInto(query, hits, limit);
        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    public void Dispose()
    {
        lock (sync)
        {
            disposed = true;
            foreach (var volume in volumes) volume.Dispose();
            volumes.Clear();
        }
    }

    /// <summary>單卷索引器：打開卷句柄 → 查詢/創建 USN Journal → 枚舉 MFT → 循環讀取 Journal 增量。</summary>
    sealed class VolumeIndexer : IDisposable
    {
        public FileNameIndex Index { get; }
        public volatile bool Ready;
        readonly char drive;
        SafeFileHandle? handle;
        ulong journalId; long nextUsn;
        volatile bool closing;

        public VolumeIndexer(char drive) { this.drive = drive; Index = new FileNameIndex(drive); }

        public bool Open()
        {
            handle = CreateFile($@"\\.\{drive}:", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            return handle is { IsInvalid: false };
        }

        public void Start() => new Thread(Run) { IsBackground = true, Name = $"BeeXFileIndex-{drive}" }.Start();

        void Run()
        {
            try
            {
                if (!QueryJournal()) return;
                EnumerateMft();
                Ready = true;
                ReadJournalLoop();
            }
            catch { }
        }

        bool QueryJournal()
        {
            if (handle == null) return false;
            if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out USN_JOURNAL_DATA_V0 data, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero))
            {
                if (Marshal.GetLastWin32Error() != ERROR_JOURNAL_NOT_ACTIVE) return false;
                // 卷上尚未啟用 USN Journal：創建一個（32MB 上限，NTFS 標準做法）再重查
                var create = new CREATE_USN_JOURNAL_DATA { MaximumSize = 0x2000000, AllocationDelta = 0x400000 };
                if (!DeviceIoControl(handle, FSCTL_CREATE_USN_JOURNAL, ref create, Marshal.SizeOf<CREATE_USN_JOURNAL_DATA>(), IntPtr.Zero, 0, out _, IntPtr.Zero)) return false;
                if (!DeviceIoControl(handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, out data, Marshal.SizeOf<USN_JOURNAL_DATA_V0>(), out _, IntPtr.Zero)) return false;
            }
            journalId = data.UsnJournalID;
            nextUsn = data.NextUsn;
            return true;
        }

        void EnumerateMft()
        {
            if (handle == null) return;
            var buffer = new byte[1 << 20];
            var start = 0UL;
            while (!closing)
            {
                var input = new MFT_ENUM_DATA_V0 { StartFileReferenceNumber = start, LowUsn = 0, HighUsn = long.MaxValue };
                if (!DeviceIoControl(handle, FSCTL_ENUM_USN_DATA, ref input, Marshal.SizeOf<MFT_ENUM_DATA_V0>(), buffer, buffer.Length, out var bytes, IntPtr.Zero)) break; // ERROR_HANDLE_EOF＝枚舉完成
                if (bytes < 8) break;
                start = BitConverter.ToUInt64(buffer, 0);
                ApplyRecords(buffer, 8, bytes, fromEnum: true);
            }
        }

        void ReadJournalLoop()
        {
            if (handle == null) return;
            var buffer = new byte[256 * 1024];
            while (!closing)
            {
                // BytesToWaitFor=1：阻塞直到卷上有新變更；Dispose 關閉句柄會使調用失敗從而退出線程
                var input = new READ_USN_JOURNAL_DATA_V0 { StartUsn = nextUsn, ReasonMask = REASON_MASK, ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 1, UsnJournalID = journalId };
                if (!DeviceIoControl(handle, FSCTL_READ_USN_JOURNAL, ref input, Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(), buffer, buffer.Length, out var bytes, IntPtr.Zero))
                {
                    if (closing) return;
                    // Journal 被重建/回繞（如 chkdsk、日誌溢出）：重新拿 ID 與起點後繼續
                    if (!QueryJournal()) return;
                    continue;
                }
                if (bytes < 8) continue;
                nextUsn = BitConverter.ToInt64(buffer, 0);
                ApplyRecords(buffer, 8, bytes, fromEnum: false);
            }
        }

        void ApplyRecords(byte[] buffer, int offset, int total, bool fromEnum)
        {
            // USN_RECORD_V2 布局：0 RecordLength,8 FRN,16 ParentFRN,40 Reason,52 FileAttributes,56 NameLength,58 NameOffset,60 Name
            var pos = offset;
            while (pos + 60 <= total)
            {
                var length = BitConverter.ToInt32(buffer, pos);
                if (length <= 0 || pos + length > total) break;
                var frn = BitConverter.ToUInt64(buffer, pos + 8);
                var parent = BitConverter.ToUInt64(buffer, pos + 16);
                var reason = BitConverter.ToUInt32(buffer, pos + 40);
                var attributes = BitConverter.ToUInt32(buffer, pos + 52);
                var nameLength = BitConverter.ToUInt16(buffer, pos + 56);
                var nameOffset = BitConverter.ToUInt16(buffer, pos + 58);
                if (pos + nameOffset + nameLength <= total && nameLength > 0)
                {
                    var name = Encoding.Unicode.GetString(buffer, pos + nameOffset, nameLength);
                    if (name.Length > 0 && name[0] != '$') // 跳過 $MFT 等 NTFS 元文件
                    {
                        var isDir = (attributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                        if (fromEnum) Index.Set(frn, parent, name, isDir);
                        else if ((reason & USN_REASON_FILE_DELETE) != 0) Index.Remove(frn);
                        else if ((reason & (USN_REASON_FILE_CREATE | USN_REASON_RENAME_NEW_NAME)) != 0) Index.Set(frn, parent, name, isDir);
                    }
                }
                pos += length;
            }
        }

        public void Dispose()
        {
            closing = true;
            try { handle?.Dispose(); } catch { } // 關閉句柄解除 ReadJournalLoop 的阻塞
            handle = null;
        }

        const uint GENERIC_READ = 0x80000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;
        const uint FSCTL_ENUM_USN_DATA = 0x900B3, FSCTL_READ_USN_JOURNAL = 0x900BB, FSCTL_QUERY_USN_JOURNAL = 0x900F4, FSCTL_CREATE_USN_JOURNAL = 0x900E7;
        const uint USN_REASON_FILE_CREATE = 0x100, USN_REASON_FILE_DELETE = 0x200, USN_REASON_RENAME_NEW_NAME = 0x2000;
        const uint REASON_MASK = USN_REASON_FILE_CREATE | USN_REASON_FILE_DELETE | USN_REASON_RENAME_NEW_NAME;
        const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const int ERROR_JOURNAL_NOT_ACTIVE = 1179;

        [StructLayout(LayoutKind.Sequential)] struct MFT_ENUM_DATA_V0 { public ulong StartFileReferenceNumber; public long LowUsn; public long HighUsn; }
        [StructLayout(LayoutKind.Sequential)] struct READ_USN_JOURNAL_DATA_V0 { public long StartUsn; public uint ReasonMask; public uint ReturnOnlyOnClose; public ulong Timeout; public ulong BytesToWaitFor; public ulong UsnJournalID; }
        [StructLayout(LayoutKind.Sequential)] struct USN_JOURNAL_DATA_V0 { public ulong UsnJournalID; public long FirstUsn; public long NextUsn; public long LowestValidUsn; public long MaxUsn; public ulong MaximumSize; public ulong AllocationDelta; }
        [StructLayout(LayoutKind.Sequential)] struct CREATE_USN_JOURNAL_DATA { public ulong MaximumSize; public ulong AllocationDelta; }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref MFT_ENUM_DATA_V0 lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref READ_USN_JOURNAL_DATA_V0 lpInBuffer, int nInBufferSize, byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer, int nInBufferSize, out USN_JOURNAL_DATA_V0 lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, ref CREATE_USN_JOURNAL_DATA lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);
    }
}
