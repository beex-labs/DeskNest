using System.IO;
using System.Text.Json;

namespace BeeX.DeskNest;

/// <summary>
/// BeeXPaths migration methods: one-time relocation and rewriting of legacy scattered data.
/// </summary>
public static partial class BeeXPaths
{
    // ---- Legacy scattered locations (only for one-time migration and compatibility mirroring) ----
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

    /// <summary>Mirror config.json to the legacy path after writing: the standalone OCR sidecar exe still reads the DeepL key from the old location.</summary>
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

    /// <summary>Whether a one-time legacy data migration is needed (pointer not created and data exists in any legacy location).</summary>
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
    /// One-time full migration on first launch after upgrade: moves legacy data scattered across AppData / Pictures / Documents into the new root directory,
    /// and rewrites the file-box paths in state.json. The progress callback reports the current item name (called off the UI thread).
    /// </summary>
    public static void MigrateLegacyIfNeeded(Action<string>? progress = null)
    {
        if (!NeedsLegacyMigration) { EnsureLayout(); return; }
        EnsureLayout();

        // Data: state, config, editor data, cache (wwwroot is regenerable but taken along too, saving one extraction)
        MoveFile(Path.Combine(LegacyDataDir, "state.json"), StateFile, progress, "state.json");
        MoveFile(Path.Combine(LegacyDataDir, "config.json"), ConfigFile, progress, "config.json");
        MoveDir(Path.Combine(LegacyDataDir, "write"), Path.Combine(DataDir, "write"), progress, "write");
        MoveDir(Path.Combine(LegacyDataDir, "wwwroot"), Path.Combine(DataDir, "wwwroot"), progress, "wwwroot");
        MoveDir(Path.Combine(LegacyDataDir, "lyrics-cache"), Path.Combine(DataDir, "lyrics-cache"), progress, "lyrics-cache");
        MoveDir(Path.Combine(LegacyDataDir, "artwork-cache"), Path.Combine(DataDir, "artwork-cache"), progress, "artwork-cache");
        MoveDir(Path.Combine(LegacyDataDir, "notes"), NotesDir, progress, "Notes");

        // Components: ffmpeg (with fallback to the spaced legacy directory) and the OCR sidecar (600MB-scale, slow across volumes)
        MoveDir(Path.Combine(LegacyDataDir, "ffmpeg"), FfmpegDir, progress, "ffmpeg");
        if (!File.Exists(Path.Combine(FfmpegDir, "ffmpeg.exe")))
            MoveDir(LegacySpacedFfmpegDir, FfmpegDir, progress, "ffmpeg");
        MoveDir(Path.Combine(LegacyDataDir, "beex-ocr"), OcrDir, progress, "OCR");
        MoveFile(Path.Combine(LegacyDataDir, "beex-ocr.stamp"), OcrDir + ".stamp", progress, "OCR stamp");

        // User-visible files: screenshots / clipboard images / recordings / file boxes
        MoveDir(Path.Combine(LegacyPicturesDir, "螢幕截圖"), ScreenshotsDir, progress, "Screenshots");
        MoveDir(Path.Combine(LegacyPicturesDir, "剪貼板圖片"), ClipboardDir, progress, "ClipboardImages");
        MoveDir(Path.Combine(LegacyPicturesDir, "螢幕錄製"), RecordingsDir, progress, "Recordings");
        MoveDir(LegacyDocsDir, FileBoxesDir, progress, "FileBoxes");

        // Cleaner logs and registry backups
        MoveDir(Path.Combine(LegacyCleanerDir, "Logs"), Path.Combine(CleanerDir, "Logs"), progress, "Cleaner Logs");
        MoveDir(Path.Combine(LegacyCleanerDir, "Backups"), Path.Combine(CleanerDir, "Backups"), progress, "Cleaner Backups");

        RewriteStatePaths(LegacyDocsDir, FileBoxesDir, clearImageOverrides: true);
        MirrorConfigToLegacy();
        DeleteIfEmptyTree(LegacyPicturesDir);
        DeleteIfEmptyTree(LegacyDataDir);
        DeleteIfEmptyTree(Path.GetDirectoryName(LegacySpacedFfmpegDir)!);
        DeleteIfEmptyTree(LegacyCleanerDir);
    }

    /// <summary>From a non-BeeX-exclusive polluted root, move only BeeX-owned content (known subitems + BeeX_ filename prefix + file-box naming); never touch the user's own files.</summary>
    static void MoveOwnedContent(string oldRoot, string newRoot, Action<string>? progress)
    {
        // Data / Components / Cleaner: move only known subitems (these directory names may have been merged with user directories of the same name)
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
        // Media directories: move only output files with the BeeX_ prefix
        foreach (var dir in new[]{"Screenshots","Recordings","ClipboardImages"})
        {
            var src = Path.Combine(oldRoot, dir);
            if (!Directory.Exists(src)) continue;
            progress?.Invoke(dir);
            foreach (var f in Directory.EnumerateFiles(src, "BeeX_*", SearchOption.TopDirectoryOnly))
                MoveFileCore(f, Path.Combine(newRoot, dir, Path.GetFileName(f)));
            DeleteIfEmptyTree(src);
        }
        // Notes: move only quick notes with the app naming pattern (yyyyMMdd-HHmmss-xxxxxx.md)
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
        // FileBoxes: move only subfolders whose names start with the file-box prefix
        var boxes = Path.Combine(oldRoot, "FileBoxes");
        if (Directory.Exists(boxes))
        {
            progress?.Invoke("FileBoxes");
            foreach (var d in Directory.EnumerateDirectories(boxes, "檔案盒*", SearchOption.TopDirectoryOnly))
                MoveDirCore(d, Path.Combine(newRoot, "FileBoxes", Path.GetFileName(d)));
            DeleteIfEmptyTree(boxes);
        }
    }

    /// <summary>Rewrites the FolderPath prefix of file-box widgets (ManagedFiles) in state.json, and can clear the old picture-directory customization.</summary>
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

    // ---- Move primitives: same-volume Move, cross-volume copy+delete; merge item-by-item when the target already exists ----
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
            catch { /* Spans multiple volumes or is in use: Use copy and merge */ }
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
