using System.IO;

namespace BeeXCleaner.Infrastructure;

/// <summary>
/// Application Data Directory Explanation: The root directory for backups and logs.
/// Preferably %ProgramData%\BeeXCleaner (the application requires Administrator privileges and is writable),
/// If the operation fails, fall back to %LOCALAPPDATA%\BeeXCleaner, then to the temporary directory, to ensure the data is always saved to disk.
/// </summary>
public static class AppPaths
{
    public const string AppFolderName = "BeeXCleaner";

    private static readonly Lazy<string> _root = new(ResolveRoot);

    /// <summary>Data root directory (confirmed to exist). </summary>
    public static string Root => _root.Value;

    /// <summary>The registry backup root directory is Backups\. </summary>
    public static string BackupsRoot => EnsureDir(Path.Combine(Root, "Backups"));

    /// <summary>Log root directory: Logs\. </summary>
    public static string LogsRoot => EnsureDir(Path.Combine(Root, "Logs"));

    private static string ResolveRoot()
    {
        // Preferably, use the "Cleaner" subdirectory under the BeeX root directory (the location is centrally controlled via the settings page).
        try
        {
            var unified = BeeX.DeskNest.BeeXPaths.CleanerDir;
            Directory.CreateDirectory(unified);
            return unified;
        }
        catch { /* Fall back to the old chain when the unified root is unavailable */ }
        foreach (var special in new[]
        {
            Environment.SpecialFolder.CommonApplicationData, // %ProgramData%
            Environment.SpecialFolder.LocalApplicationData   // %LOCALAPPDATA%
        })
        {
            try
            {
                var baseDir = Environment.GetFolderPath(special);
                if (string.IsNullOrEmpty(baseDir)) continue;
                var dir = Path.Combine(baseDir, AppFolderName);
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch { /* Try the next candidate */ }
        }

        // Last resort: Temporary directory (almost always writable)
        var tmp = Path.Combine(Path.GetTempPath(), AppFolderName);
        try { Directory.CreateDirectory(tmp); } catch { /* Ignore */ }
        return tmp;
    }

    private static string EnsureDir(string dir)
    {
        try { Directory.CreateDirectory(dir); } catch { /* Ignore */ }
        return dir;
    }
}
