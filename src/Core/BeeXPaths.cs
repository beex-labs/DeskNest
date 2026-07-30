using System.IO;

namespace BeeX.DeskNest;

/// <summary>
/// BeeX 統一資料根目錄：所有用戶資料（截圖/錄屏/剪貼板圖片/便籤/收納/設定/緩存/元件/清理器日誌）
/// 全部收口在單一 BeeX 資料夾下，由設定頁統一控制。
/// 默認 D:\BeeX（無可寫 D 盤則回退 C:\BeeX）；根目錄指針存於 %LocalAppData%\BeeX\root.txt
/// （指針不能存在根目錄自身內，否則換目錄後找不到自己）。
/// 目錄規劃：Data\（state/config/write/wwwroot/歌詞封面緩存）、Components\（ffmpeg/beex-ocr）、
/// Screenshots\、Recordings\、ClipboardImages\、Notes\、FileBoxes\、Cleaner\。
/// </summary>
public static partial class BeeXPaths
{
    static readonly object gate = new();
    static string? cachedRoot;

    static string PointerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeX", "root.txt");

    /// <summary>當前根目錄（含解析與指針落盤，線程安全，結果緩存）。</summary>
    public static string Root
    {
        get
        {
            lock (gate)
            {
                if (cachedRoot != null) return cachedRoot;
                cachedRoot = ResolveRoot();
                return cachedRoot;
            }
        }
    }

    public static string DataDir => Path.Combine(Root, "Data");
    public static string ComponentsDir => Path.Combine(Root, "Components");
    public static string FfmpegDir => Path.Combine(ComponentsDir, "ffmpeg");
    public static string OcrDir => Path.Combine(ComponentsDir, "beex-ocr");
    public static string ScreenshotsDir => Path.Combine(Root, "Screenshots");
    public static string RecordingsDir => Path.Combine(Root, "Recordings");
    public static string ClipboardDir => Path.Combine(Root, "ClipboardImages");
    public static string NotesDir => Path.Combine(Root, "Notes");
    public static string FileBoxesDir => Path.Combine(Root, "FileBoxes");
    public static string CleanerDir => Path.Combine(Root, "Cleaner");
    public static string StateFile => Path.Combine(DataDir, "state.json");
    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    static string ResolveRoot()
    {
        try
        {
            if (File.Exists(PointerFile))
            {
                var pointed = File.ReadAllText(PointerFile).Trim();
                if (pointed.Length > 0 && Path.IsPathFullyQualified(pointed))
                {
                    // 指針位置可用直接採信；根盤被拔出/不可寫時回退默認但不覆蓋指針（盤回來後恢復）
                    if (EnsureWritable(pointed)) return pointed;
                    var fallback = DefaultRoot();
                    EnsureWritable(fallback);
                    return fallback;
                }
            }
        }
        catch { }
        var root = DefaultRoot();
        if (!EnsureWritable(root) && !string.Equals(root, @"C:\BeeX", StringComparison.OrdinalIgnoreCase))
        {
            root = @"C:\BeeX";
            EnsureWritable(root);
        }
        WritePointer(root);
        return root;
    }

    static string DefaultRoot()
    {
        try
        {
            var d = new DriveInfo("D");
            if (d.DriveType == DriveType.Fixed && d.IsReady && EnsureWritable(@"D:\BeeX")) return @"D:\BeeX";
        }
        catch { }
        return @"C:\BeeX";
    }

    static bool EnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".beex-write-probe");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    static void WritePointer(string root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PointerFile)!);
            File.WriteAllText(PointerFile, root);
        }
        catch { }
    }

    /// <summary>建立標準子目錄骨架（冪等）。</summary>
    public static void EnsureLayout()
    {
        foreach (var dir in new[] { DataDir, ComponentsDir, ScreenshotsDir, RecordingsDir, ClipboardDir, NotesDir, FileBoxesDir, CleanerDir })
            try { Directory.CreateDirectory(dir); } catch { }
    }

    /// <summary>BeeX 擁有的頂層目錄清單（根目錄下只會有這些）。</summary>
    static readonly string[] TopLevelDirs={"Data","Components","Screenshots","Recordings","ClipboardImages","Notes","FileBoxes","Cleaner"};

    /// <summary>規範化根目錄：空資料夾（用戶專門新建的）直接用；目標已有其他內容且不叫 BeeX 時才追加 BeeX 子目錄，避免把資料散進用戶既有文件裡。</summary>
    public static string NormalizeRoot(string path)
    {
        path=Path.GetFullPath(path.Trim());
        var leaf=Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if(string.Equals(leaf,"BeeX",StringComparison.OrdinalIgnoreCase))return path;
        try{if(!Directory.Exists(path)||!Directory.EnumerateFileSystemEntries(path).Any())return path;}catch{}
        return Path.Combine(path,"BeeX");
    }

    /// <summary>
    /// 設定頁更改根目錄：整體搬遷資料到 newRoot 並改寫指針與 state.json 路徑。
    /// 同卷用 Move（瞬間），跨卷複製+刪除。失敗拋異常由調用方提示；成功後建議重啟進程。
    /// 舊根若不是 BeeX 專屬目錄（曾被錯誤地散進用戶資料夾），只按所有權清單搬 BeeX 自己的內容，絕不觸碰用戶文件。
    /// </summary>
    public static void ChangeRoot(string newRoot, Action<string>? progress = null)
    {
        newRoot = NormalizeRoot(newRoot);
        var oldRoot = Root;
        var trimOld = Path.TrimEndingDirectorySeparator(oldRoot);
        if (string.Equals(Path.TrimEndingDirectorySeparator(newRoot), trimOld, StringComparison.OrdinalIgnoreCase)) return;
        // 目標在舊根內部只允許一種情況：整理到它自己的 BeeX 子目錄（搬移時會跳過目標自身，不會遞迴自吞）
        var insideOld = newRoot.StartsWith(trimOld + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (insideOld && !string.Equals(Path.TrimEndingDirectorySeparator(newRoot), Path.Combine(trimOld, "BeeX"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新位置不能位於當前資料夾內部。");
        if (!EnsureWritable(newRoot))
            throw new InvalidOperationException("目標資料夾無法寫入或空間不足");
        // 跨卷時校驗剩餘空間：只算 BeeX 擁有的目錄（髊根裡用戶自己的文件不搬也不計空間）
        if (!SameVolume(oldRoot, newRoot))
        {
            long required = 0;
            foreach (var name in TopLevelDirs)
            {
                var p = Path.Combine(oldRoot, name);
                if (Directory.Exists(p)) required += DirectorySize(new DirectoryInfo(p));
            }
            var free = new DriveInfo(Path.GetPathRoot(newRoot)!).AvailableFreeSpace;
            if (free < required + (100L << 20))
                throw new InvalidOperationException("目標資料夾無法寫入或空間不足");
        }

        // 停掉佔用元件的進程句柄，避免移動失敗
        try { OcrSidecarService.Shutdown(); } catch { }
        FfmpegService.Invalidate();

        var oldIsBeeXNamed = string.Equals(Path.GetFileName(trimOld), "BeeX", StringComparison.OrdinalIgnoreCase);
        // 乾淨根判定不看目錄名而看內容：頂層只有 BeeX 擁有的目錄（允許 desktop.ini）才整體搬；否則視為被汙染的髊根，只按所有權清單搬
        bool oldIsClean;
        try
        {
            oldIsClean=Directory.Exists(oldRoot)&&Directory.EnumerateFileSystemEntries(oldRoot).All(p=>
            {
                if(string.Equals(Path.TrimEndingDirectorySeparator(p),Path.TrimEndingDirectorySeparator(newRoot),StringComparison.OrdinalIgnoreCase))return true;
                var n=Path.GetFileName(p);
                return TopLevelDirs.Contains(n,StringComparer.OrdinalIgnoreCase)||string.Equals(n,"desktop.ini",StringComparison.OrdinalIgnoreCase);
            });
        }
        catch{oldIsClean=oldIsBeeXNamed;}
        if (oldIsClean)
        {
            // 乾淨的 BeeX 專屬根：整體搬走全部內容（跳過目標目錄自身，防止目標在舊根內部時自吞）
            foreach (var entry in Directory.EnumerateFileSystemEntries(oldRoot))
            {
                if(string.Equals(Path.TrimEndingDirectorySeparator(entry),Path.TrimEndingDirectorySeparator(newRoot),StringComparison.OrdinalIgnoreCase))continue;
                var name = Path.GetFileName(entry);
                progress?.Invoke(name);
                var target = Path.Combine(newRoot, name);
                if (Directory.Exists(entry)) MoveDirCore(entry, target);
                else MoveFileCore(entry, target);
            }
        }
        else
        {
            MoveOwnedContent(oldRoot, newRoot, progress);
        }

        lock (gate) cachedRoot = newRoot;
        WritePointer(newRoot);
        RewriteStatePaths(Path.Combine(oldRoot, "FileBoxes"), FileBoxesDir, clearImageOverrides: false);
        MirrorConfigToLegacy();
        DeleteIfEmptyTree(oldRoot); // 只在舊根已無任何文件時才會刪除，用戶自己的文件存在則原封不動
    }
}
