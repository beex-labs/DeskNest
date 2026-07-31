using System.IO;

namespace BeeX.DeskNest;

/// <summary>
/// Unified BeeX data root directory: all user data (screenshots/recordings/clipboard images/notes/file boxes/settings/cache/components/cleaner logs)
/// is consolidated under a single BeeX folder, controlled centrally by the settings page.
/// Defaults to D:\BeeX (falls back to C:\BeeX if D: is not writable); the root pointer is stored in %LocalAppData%\BeeX\root.txt
/// (the pointer cannot live inside the root itself, otherwise it could not find itself after the directory is changed).
/// Directory layout: Data\ (state/config/write/wwwroot/lyrics cover cache), Components\ (ffmpeg/beex-ocr),
/// Screenshots\, Recordings\, ClipboardImages\, Notes\, FileBoxes\, Cleaner\.
/// </summary>
public static partial class BeeXPaths
{
    static readonly object gate = new();
    static string? cachedRoot;

    static string PointerFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BeeX", "root.txt");

    /// <summary>Current root directory (with resolution and pointer persistence; thread-safe, result cached).</summary>
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
                    // The pointer location can be trusted directly when usable; when the root drive is removed/unwritable, fall back to the default but do not overwrite the pointer (it recovers when the drive returns)
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

    /// <summary>Creates the standard subdirectory skeleton (idempotent).</summary>
    public static void EnsureLayout()
    {
        foreach (var dir in new[] { DataDir, ComponentsDir, ScreenshotsDir, RecordingsDir, ClipboardDir, NotesDir, FileBoxesDir, CleanerDir })
            try { Directory.CreateDirectory(dir); } catch { }
    }

    /// <summary>List of top-level directories owned by BeeX (only these exist under the root).</summary>
    static readonly string[] TopLevelDirs={"Data","Components","Screenshots","Recordings","ClipboardImages","Notes","FileBoxes","Cleaner"};

    /// <summary>Normalizes the root directory: an empty folder (deliberately created by the user) is used directly; a BeeX subdirectory is appended only when the target already has other content and is not named BeeX, to avoid scattering data into the user's existing files.</summary>
    public static string NormalizeRoot(string path)
    {
        path=Path.GetFullPath(path.Trim());
        var leaf=Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        if(string.Equals(leaf,"BeeX",StringComparison.OrdinalIgnoreCase))return path;
        try{if(!Directory.Exists(path)||!Directory.EnumerateFileSystemEntries(path).Any())return path;}catch{}
        return Path.Combine(path,"BeeX");
    }

    /// <summary>
    /// Settings page changes the root directory: migrates all data to newRoot and rewrites the pointer and state.json paths.
    /// Uses Move within the same volume (instant), copy+delete across volumes. On failure it throws for the caller to surface; a process restart is recommended after success.
    /// If the old root is not a BeeX-exclusive directory (previously scattered into a user folder by mistake), only BeeX's own content is moved per the ownership list, never touching user files.
    /// </summary>
    public static void ChangeRoot(string newRoot, Action<string>? progress = null)
    {
        newRoot = NormalizeRoot(newRoot);
        var oldRoot = Root;
        var trimOld = Path.TrimEndingDirectorySeparator(oldRoot);
        if (string.Equals(Path.TrimEndingDirectorySeparator(newRoot), trimOld, StringComparison.OrdinalIgnoreCase)) return;
        // The target being inside the old root is only allowed in one case: reorganizing into its own BeeX subdirectory (the move skips the target itself, so it does not recursively swallow itself)
        var insideOld = newRoot.StartsWith(trimOld + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (insideOld && !string.Equals(Path.TrimEndingDirectorySeparator(newRoot), Path.Combine(trimOld, "BeeX"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("新位置不能位於當前資料夾內部。");
        if (!EnsureWritable(newRoot))
            throw new InvalidOperationException("目標資料夾無法寫入或空間不足");
        // Across volumes, validate free space: count only BeeX-owned directories (the user's own files in a polluted root are neither moved nor counted)
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

        // Stop process handles holding the components to avoid a failed move
        try { OcrSidecarService.Shutdown(); } catch { }
        FfmpegService.Invalidate();

        var oldIsBeeXNamed = string.Equals(Path.GetFileName(trimOld), "BeeX", StringComparison.OrdinalIgnoreCase);
        // A clean root is determined by content, not directory name: only when the top level has just BeeX-owned directories (desktop.ini allowed) is everything moved; otherwise it is treated as a polluted root and only the ownership list is moved
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
            // Clean BeeX-exclusive root: move all content wholesale (skipping the target directory itself, to prevent self-swallowing when the target is inside the old root)
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
        DeleteIfEmptyTree(oldRoot); // Only deletes when the old root has no files left; if the user's own files exist, it is left untouched
    }
}
