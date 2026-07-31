using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace BeeX.DeskNest;

/// <summary>A single file match: name, full path, whether it is a directory, and the match score (higher ranks first).</summary>
public readonly record struct FileHit(string Name, string FullPath, bool IsDirectory, int Score);

/// <summary>
/// In-memory file-name index for one volume: FRN -&gt; (parent FRN, name, is-directory).
/// Paths are not stored; they are rebuilt by walking the parent chain on query. A reader/writer lock keeps
/// incremental updates and searches thread-safe. Contains no Win32 dependency, so it is directly unit-testable.
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
        // Walk up the parent-FRN chain to build the path; depth limit guards against cycles (a normal directory tree stays well under 64 levels).
        var parts = new List<string>(8);
        var current = frn; var depth = 0;
        while (depth++ < 64 && entries.TryGetValue(current, out var e)) { parts.Add(e.Name); current = e.Parent; }
        parts.Reverse();
        return root + string.Join('\\', parts);
    }

    /// <summary>Space-tokenized AND match (case-insensitive); ranks exact &gt; prefix &gt; substring, shorter names first; results are appended to <paramref name="results"/>.</summary>
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
/// Whole-disk file-name index service: for each fixed NTFS volume it enumerates the MFT
/// (FSCTL_ENUM_USN_DATA) to build the file-name index in seconds, then a background thread
/// watches the USN Journal (FSCTL_READ_USN_JOURNAL) for real-time incremental updates.
/// Opening a volume handle requires administrator rights (the app manifest sets requireAdministrator).
/// </summary>
public sealed class FileIndexService : IDisposable
{
    readonly List<VolumeIndexer> volumes = [];
    readonly object sync = new();
    bool started, disposed;

    /// <summary>True when at least one volume opened successfully (false without elevation or NTFS volumes; callers should fall back).</summary>
    public bool Available { get { lock (sync) return volumes.Count > 0; } }
    /// <summary>True when every volume has finished its first MFT enumeration and can return complete results.</summary>
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

    /// <summary>Per-volume indexer: open the volume handle -&gt; query/create the USN Journal -&gt; enumerate the MFT -&gt; loop reading Journal increments.</summary>
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
                // The volume has no active USN Journal yet: create one (32MB cap) and query again.
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
                if (!DeviceIoControl(handle, FSCTL_ENUM_USN_DATA, ref input, Marshal.SizeOf<MFT_ENUM_DATA_V0>(), buffer, buffer.Length, out var bytes, IntPtr.Zero)) break; // ERROR_HANDLE_EOF = enumeration complete
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
                // BytesToWaitFor=1: block until the volume has a new change; disposing the handle makes the call fail so the thread exits.
                var input = new READ_USN_JOURNAL_DATA_V0 { StartUsn = nextUsn, ReasonMask = REASON_MASK, ReturnOnlyOnClose = 0, Timeout = 0, BytesToWaitFor = 1, UsnJournalID = journalId };
                if (!DeviceIoControl(handle, FSCTL_READ_USN_JOURNAL, ref input, Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(), buffer, buffer.Length, out var bytes, IntPtr.Zero))
                {
                    if (closing) return;
                    // The Journal was rebuilt or wrapped (e.g. chkdsk, log overflow): re-fetch the ID and start point, then continue.
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
            // USN_RECORD_V2 layout: 0 RecordLength, 8 FRN, 16 ParentFRN, 40 Reason, 52 FileAttributes, 56 NameLength, 58 NameOffset, 60 Name
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
                    if (name.Length > 0 && name[0] != '$') // skip $MFT and other NTFS metadata files
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
            try { handle?.Dispose(); } catch { } // closing the handle unblocks ReadJournalLoop
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
