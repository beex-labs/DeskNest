using System;
using System.IO;
using System.Linq;

namespace BeexWrite;

/// <summary>
/// Host shim for running the BeexWrite editor embedded inside BeeX DeskNest.
/// Replaces the standalone App-level services (single-instance mutex, data
/// directories, OS theme detection) with DeskNest-provided equivalents.
/// </summary>
public static class WriteHost
{
    /// <summary>Draft recovery is disabled in embedded mode: every note window owns a
    /// concrete .md file and auto-save persists directly to it, so a single shared
    /// draft slot would only let multiple note windows clobber each other.</summary>
    public static readonly bool IsPrimaryInstance = false;

    /// <summary>DeskNest data root (unified BeeX root, Data subfolder).</summary>
    public static string DataDirectory => BeeX.DeskNest.BeeXPaths.DataDir;

    /// <summary>Editor settings/shortcuts/locales/recovery live here.</summary>
    public static string WriteDataDirectory => Path.Combine(DataDirectory, "write");

    /// <summary>Markdown capture notes created from the DeskNest capture widget.</summary>
    public static string NotesDirectory => BeeX.DeskNest.BeeXPaths.NotesDir;

    /// <summary>Set by DeskNest: returns true when the host "Dark" theme is active.
    /// The editor palette follows the host instead of the OS / its own setting.</summary>
    public static Func<bool>? IsHostDark { get; set; }

    /// <summary>Set by DeskNest: returns the host UI locale ("zh-TW", "zh-CN" or "en").</summary>
    public static Func<string>? HostLocale { get; set; }

    /// <summary>Set by DeskNest: called when an editor window gains focus so the host
    /// can unregister its global hotkeys (and re-register them on the way out).</summary>
    public static Action? SuspendHostHotkeys { get; set; }
    public static Action? ResumeHostHotkeys { get; set; }

    // ---- embedded web editor assets -----------------------------------------

    private const string WebAssetPrefix = "BeeX.DeskNest.wwwroot.";
    private static string? _webAssetsDir;

    /// <summary>
    /// Returns the on-disk wwwroot folder for the WebView2 virtual-host mapping.
    /// The editor bundle ships embedded in the exe; it is extracted once per build
    /// (keyed by the assembly MVID) to %LocalAppData%\BeeX\DeskNest\wwwroot, since
    /// Chromium can only serve real files, not managed resources. Falls back to a
    /// loose wwwroot folder next to the exe when no embedded assets are present.
    /// </summary>
    public static string EnsureWebAssets()
    {
        if (_webAssetsDir != null) return _webAssetsDir;

        var targetDir = Path.Combine(DataDirectory, "wwwroot");
        var marker = Path.Combine(targetDir, ".version");
        string stamp;
        try { stamp = typeof(WriteHost).Assembly.ManifestModule.ModuleVersionId.ToString("N"); }
        catch { stamp = "0"; }

        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp &&
                File.Exists(Path.Combine(targetDir, "index.html")))
                return _webAssetsDir = targetDir;

            var assembly = typeof(WriteHost).Assembly;
            var names = assembly.GetManifestResourceNames()
                .Where(n => n.StartsWith(WebAssetPrefix, StringComparison.Ordinal)).ToList();
            if (names.Count == 0) return _webAssetsDir = FallbackWebAssets();

            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            foreach (var name in names)
            {
                var relative = name[WebAssetPrefix.Length..]
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(destination);
                source.CopyTo(file);
            }
            File.WriteAllText(marker, stamp);
            return _webAssetsDir = targetDir;
        }
        catch
        {
            // Extraction failed (disk full, AV lock, ...) — try the loose folder.
            return _webAssetsDir = FallbackWebAssets();
        }
    }

    private static string FallbackWebAssets() =>
        Path.Combine(AppContext.BaseDirectory, "wwwroot");
}
