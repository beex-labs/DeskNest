using System.IO;
using System.Text.Json;

namespace BeeX.DeskNest;

/// <summary>
/// BeeXPaths 遷移相關方法：舊版散落資料的一次性搬遷與改寫。
/// </summary>
public static partial class BeeXPaths
{
    // ---- 舊版散落位置（僅供一次性遷移與兼容鏡像）----
    public static string LegacyDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeX", "DeskNest");
    public static string LegacyConfigFile => Path.Combine(LegacyDataDir, "config.json");
    static string LegacySpacedFfmpegDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeX DeskNest", "ffmpeg");
    static string LegacyPicturesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "BeeX 圖片");
    static string LegacyDocsDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "BeeX DeskNest");
    static string LegacyCleanerDir
    {
        get
        {
            var pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BeeXCleaner");
            if (Directory.Exists(pd)) return pd;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeXCleaner");
        }
    }

    /// <summary>config.json 寫入後鏡像到舊路徑：OCR 側車獨立 exe 仍從舊位置讀 DeepL Key</summary>
    public static void MirrorConfigToLegacy()
    {
        try
        {
            if (!File.Exists(ConfigFile)) return;
            Directory.CreateDirectory(LegacyDataDir);
            File.Copy(ConfigFile, LegacyConfigFile, overwrite: true);
        }
        catch { }
    }

    /// <summary>是否需要執行一次舊資料遷移（指針未建立且存在任一舊位置資料）。</summary>
    public static bool NeedsLegacyMigration
    {
        get
        {
            try
            {
                if (File.Exists(PointerFile)) return false;
                return File.Exists(Path.Combine(LegacyDataDir, "state.json")) ||
                       Directory.Exists(LegacyPicturesDir) ||
                       Directory.Exists(LegacyDocsDir) ||
                       Directory.Exists(Path.Combine(LegacyDataDir, "beex-ocr")) ||
                       Directory.Exists(Path.Combine(LegacyDataDir, "ffmpeg"));
            }
            catch { return false; }
        }
    }

    /// <summary>
    /// 升級後首次啟動的一次性整體遷移：把散落在 AppData/圖片庫/文檔庫 的舊資料搬進新根目錄，
    /// 並改寫 state.json 中收納格子的路徑。progress 回調為當前項目名稱（UI 線程外調用）。
    /// </summary>
    public static void MigrateLegacyIfNeeded(Action<string>? progress = null)
    {
        if (!NeedsLegacyMigration) { EnsureLayout(); return; }
        EnsureLayout();

        // Data：狀態、配置、編輯器資料、緩存（wwwroot 可再生也一併帶走，省一次解壓）
        MoveFile(Path.Combine(LegacyDataDir, "state.json"), StateFile, progress, "state.json");
        MoveFile(Path.Combine(LegacyDataDir, "config.json"), ConfigFile, progress, "config.json");
        MoveDir(Path.Combine(LegacyDataDir, "write"), Path.Combine(DataDir, "write"), progress, "write");
        MoveDir(Path.Combine(LegacyDataDir, "wwwroot"), Path.Combine(DataDir, "wwwroot"), progress, "wwwroot");
        MoveDir(Path.Combine(LegacyDataDir, "lyrics-cache"), Path.Combine(DataDir, "lyrics-cache"), progress, "lyrics-cache");
        MoveDir(Path.Combine(LegacyDataDir, "artwork-cache"), Path.Combine(DataDir, "artwork-cache"), progress, "artwork-cache");
        MoveDir(Path.Combine(LegacyDataDir, "notes"), NotesDir, progress, "Notes");

        // Components：ffmpeg（含帶空格舊目錄兜底）與 OCR 側車（600MB 級，跨卷會慢）
        MoveDir(Path.Combine(LegacyDataDir, "ffmpeg"), FfmpegDir, progress, "ffmpeg");
        if (!File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")))
            MoveDir(LegacySpacedFfmpegDir, FfmpegDir, progress, "ffmpeg");
        MoveDir(Path.Combine(LegacyDataDir, "beex-ocr"), OcrDir, progress, "OCR");
        MoveFile(Path.Combine(LegacyDataDir, "beex-ocr.stamp"), OcrDir + ".stamp", progress, "OCR stamp");

        // 用戶可見文件：截圖 / 剪貼板圖片 / 錄屏 / 收納格子
        MoveDir(Path.Combine(LegacyPicturesDir, "螢幕截圖"), ScreenshotsDir, progress, "Screenshots");
        MoveDir(Path.Combine(LegacyPicturesDir, "剪貼板圖片"), ClipboardDir, progress, "ClipboardImages");
        MoveDir(Path.Combine(LegacyPicturesDir, "螢幕錄製"), RecordingsDir, progress, "Recordings");
        MoveDir(LegacyDocsDir, FileBoxesDir, progress, "FileBoxes");

        // Cleaner 日誌與註冊表備份
        MoveDir(Path.Combine(LegacyCleanerDir, "Logs"), Path.Combine(CleanerDir, "Logs"), progress, "Cleaner Logs");
        MoveDir(Path.Combine(LegacyCleanerDir, "Backups"), Path.Combine(CleanerDir, "Backups"), progress, "Cleaner Backups");

        RewriteStatePaths(LegacyDocsDir, FileBoxesDir, clearImageOverrides: true);
        MirrorConfigToLegacy();
        DeleteIfEmptyTree(LegacyPicturesDir);
        DeleteIfEmptyTree(LegacyDataDir);
        DeleteIfEmptyTree(Path.GetDirectoryName(LegacySpacedFfmpegDir)!);
        DeleteIfEmptyTree(LegacyCleanerDir);
    }

    /// <summary>從非 BeeX 專屬的髊根目錄中只搬 BeeX 擁有的內容（已知子項 + BeeX_ 檔名前綴 + 檔案盒命名），用戶自己的文件一律不碰。</summary>
    static void MoveOwnedContent(string oldRoot, string newRoot, Action<string>? progress)
    {
        // Data / Components / Cleaner：只搬已知子項（這些目錄名可能與用戶同名目錄合併過）
        foreach (var (dir, items) in new (string Dir, string[] Items)[]{
            ("Data", new[]{"state.json","config.json","write","wwwroot","lyrics-cache","artwork-cache","backgrounds"}),
            ("Components", new[]{"ffmpeg","beex-ocr","beex-ocr.stamp"}),
            ("Cleaner", new[]{"Logs","Backups"})})
        {
            var src = Path.Combine(oldRoot, dir);
            if (!Directory.Exists(src)) continue;
            progress?.Invoke(dir);
            foreach (var item in items)
            {
                var s = Path.Combine(src, item);
                var t = Path.Combine(newRoot, dir, item);
                if (Directory.Exists(s)) MoveDirCore(s, t);
                else if (File.Exists(s)) MoveFileCore(s, t);
            }
            DeleteIfEmptyTree(src);
        }
        // 媒體目錄：只搬 BeeX_ 前綴的產出文件
        foreach (var dir in new[]{"Screenshots","Recordings","ClipboardImages"})
        {
            var src = Path.Combine(oldRoot, dir);
            if (!Directory.Exists(src)) continue;
            progress?.Invoke(dir);
            foreach (var f in Directory.EnumerateFiles(src, "BeeX_*", SearchOption.TopDirectoryOnly))
                MoveFileCore(f, Path.Combine(newRoot, dir, Path.GetFileName(f)));
            DeleteIfEmptyTree(src);
        }
        // Notes：只搬 App 命名模式的隨記（yyyyMMdd-HHmmss-xxxxxx.md）
        var notes = Path.Combine(oldRoot, "Notes");
        if (Directory.Exists(notes))
        {
            progress?.Invoke("Notes");
            var pattern = new System.Text.RegularExpressions.Regex(@"^\d{8}-\d{6}-[0-9a-f]{6}\.md$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            foreach (var f in Directory.EnumerateFiles(notes, "*.md", SearchOption.TopDirectoryOnly))
                if (pattern.IsMatch(Path.GetFileName(f)))
                    MoveFileCore(f, Path.Combine(newRoot, "Notes", Path.GetFileName(f)));
            DeleteIfEmptyTree(notes);
        }
        // FileBoxes：只搬「檔案盒」開頭的子資料夾
        var boxes = Path.Combine(oldRoot, "FileBoxes");
        if (Directory.Exists(boxes))
        {
            progress?.Invoke("FileBoxes");
            foreach (var d in Directory.EnumerateDirectories(boxes, "檔案盒*", SearchOption.TopDirectoryOnly))
                MoveDirCore(d, Path.Combine(newRoot, "FileBoxes", Path.GetFileName(d)));
            DeleteIfEmptyTree(boxes);
        }
    }

    /// <summary>改寫 state.json 中收納格子（ManagedFiles）的 FolderPath 前綴，並可清空舊的圖片目錄自定義。</summary>
    static void RewriteStatePaths(string oldPrefix, string newPrefix, bool clearImageOverrides)
    {
        try
        {
            if (!File.Exists(StateFile)) return;
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(StateFile));
            if (state == null) return;
            var changed = false;
            foreach (var nest in state.Nests.Where(n => n.Kind == NestKind.ManagedFiles && !string.IsNullOrWhiteSpace(n.FolderPath)))
            {
                if (nest.FolderPath.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    nest.FolderPath = Path.Combine(newPrefix, Path.GetRelativePath(oldPrefix, nest.FolderPath));
                    changed = true;
                }
            }
            if (clearImageOverrides && (state.ClipboardImageDirectory.Length > 0 || state.ScreenshotDirectory.Length > 0))
            {
                state.ClipboardImageDirectory = "";
                state.ScreenshotDirectory = "";
                changed = true;
            }
            if (changed)
                File.WriteAllText(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---- 移動原語：同卷 Move、跨卷複製+刪除；目標已存在時逐項合併 ----
    static void MoveFile(string source, string target, Action<string>? progress, string label)
    {
        if (!File.Exists(source)) return;
        progress?.Invoke(label);
        MoveFileCore(source, target);
    }

    static void MoveDir(string source, string target, Action<string>? progress, string label)
    {
        if (!Directory.Exists(source)) return;
        progress?.Invoke(label);
        MoveDirCore(source, target);
    }

    static void MoveFileCore(string source, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(source, target, overwrite: true);
        }
        catch
        {
            try { File.Copy(source, target, overwrite: true); File.Delete(source); } catch { }
        }
    }

    static void MoveDirCore(string source, string target)
    {
        if (!Directory.Exists(target))
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(target)!); Directory.Move(source, target); return; }
            catch { /* 跨卷或被占用：走複製合併 */ }
        }
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            MoveFileCore(file, dest);
        }
        try { Directory.Delete(source, recursive: true); } catch { }
    }

    static bool SameVolume(string a, string b)
    {
        try { return string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static long DirectorySize(DirectoryInfo dir)
    {
        long size = 0;
        try { foreach (var f in dir.EnumerateFiles("*", SearchOption.AllDirectories)) size += f.Length; } catch { }
        return size;
    }

    static void DeleteIfEmptyTree(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if (!Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Any())
                Directory.Delete(dir, recursive: true);
        }
        catch { }
    }
}
