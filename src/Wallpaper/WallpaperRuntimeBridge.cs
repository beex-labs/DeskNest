using System.IO;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace BeeX.DeskNest;

/// <summary>
/// Typed message channel between the host and a wallpaper page running inside WebView2 (same pattern as
/// BeexWrite's EditorBridge): outgoing state is posted as JSON, incoming messages surface as .NET events.
/// The JS side is the SDK in wwwroot\runtime.js (window.BeeXWallpaper).
/// </summary>
sealed class WallpaperRuntimeBridge
{
    CoreWebView2? core;

    /// <summary>The page's SDK finished booting and is ready to receive state.</summary>
    public event Action? Ready;
    /// <summary>Any other message from the page: (type, whole message root). Used by the scene editor host.</summary>
    public event Action<string, JsonElement>? MessageReceived;

    public bool IsAttached => core != null;

    public void Attach(CoreWebView2 core)
    {
        this.core = core;
        core.WebMessageReceived += (_, e) =>
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) return;
                var type = t.GetString() ?? "";
                if (type == "ready") Ready?.Invoke();
                else MessageReceived?.Invoke(type, root.Clone());
            }
            catch { }
        };
    }

    public void PostFps(int fps) => Post(new { type = "fps", value = fps });
    public void PostPause() => Post(new { type = "pause" });
    public void PostResume() => Post(new { type = "resume" });
    public void PostAudio(float[] bands, bool beat, float level) => Post(new { type = "audio", bands, beat, level });
    public void PostPointer(double x, double y, bool down) => Post(new { type = "pointer", x, y, down });
    public void PostMonitor(int width, int height, double dpi) => Post(new { type = "monitor", width, height, dpi });
    public void PostProps(Dictionary<string, string> map) => Post(new { type = "props", map });
    public void Post(object message)
    {
        try { core?.PostWebMessageAsJson(JsonSerializer.Serialize(message)); } catch { }
    }
}

/// <summary>
/// On-disk copy of the embedded wallpaper web runtime (wwwroot). Mirrors WriteHost.EnsureWebAssets: the bundle is
/// compiled into the exe and extracted once per build (keyed by the assembly MVID) because the Chromium virtual host
/// can only serve real files. Falls back to a loose folder next to the exe during development.
/// </summary>
static class WallpaperWebAssets
{
    const string Prefix = "BeeX.DeskNest.wallpaper.";
    static string? dir;

    public static string EnsureWebAssets()
    {
        if (dir != null) return dir;
        var targetDir = Path.Combine(BeeXPaths.DataDir, "wallpaper-www");
        var marker = Path.Combine(targetDir, ".version");
        string stamp;
        try { stamp = typeof(WallpaperWebAssets).Assembly.ManifestModule.ModuleVersionId.ToString("N"); }
        catch { stamp = "0"; }
        try
        {
            if (File.Exists(marker) && File.ReadAllText(marker) == stamp && File.Exists(Path.Combine(targetDir, "runtime.js")))
                return dir = targetDir;
            var assembly = typeof(WallpaperWebAssets).Assembly;
            var names = assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).ToList();
            if (names.Count == 0) return dir = Fallback();
            if (Directory.Exists(targetDir)) Directory.Delete(targetDir, true);
            foreach (var name in names)
            {
                var relative = name[Prefix.Length..].Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.Combine(targetDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                using var source = assembly.GetManifestResourceStream(name)!;
                using var file = File.Create(destination);
                source.CopyTo(file);
            }
            File.WriteAllText(marker, stamp);
            return dir = targetDir;
        }
        catch { return dir = Fallback(); }
    }

    static string Fallback() => Path.Combine(AppContext.BaseDirectory, "Wallpaper", "wwwroot");
}
