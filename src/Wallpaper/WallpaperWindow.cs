using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Brushes = System.Windows.Media.Brushes;
using Drawing = System.Drawing;

namespace BeeX.DeskNest;

/// <summary>
/// A borderless render surface for one monitor that is hosted on the desktop background layer. Every wallpaper kind
/// (video, image, web page, shader, scene) renders through WebView2: a WPF window reparented under the shell's
/// desktop host no longer presents its own D3D-composed content, so native WPF controls (MediaElement/Image) stay
/// blank, while WebView2 (own child HWND pipeline) renders fine. The frame rate is driven externally by the
/// governor: a target of 0 pauses playback (and suspends the browser process) to save power.
/// </summary>
public sealed class WallpaperWindow : Window
{
    readonly Grid root = new() { ClipToBounds = true };
    Microsoft.Web.WebView2.Wpf.WebView2? web;
    readonly WallpaperRuntimeBridge bridge = new();
    bool webSuspended;
    WallpaperItem? current;
    double globalVolume;
    double lastPostedVolume = -1;
    bool muted;
    int targetFps = 60;

    public string DeviceName { get; }
    public Drawing.Rectangle ScreenPhysical { get; private set; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;
    /// <summary>The wallpaper currently shown on this surface (used by the service to route audio/pointer data).</summary>
    public WallpaperItem? Current => current;
    /// <summary>True while a wallpaper is assigned: every kind renders through the WebView2 surface.</summary>
    public bool IsWebSurface => current != null;

    public WallpaperWindow(string deviceName, Drawing.Rectangle screenPhysical)
    {
        DeviceName = deviceName;
        ScreenPhysical = screenPhysical;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = false;
        Focusable = false;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Start off-screen with a placeholder size; the real geometry is applied in physical pixels after attaching.
        Left = -32000; Top = -32000; Width = 320; Height = 200;
        Content = root;
        SourceInitialized += (_, _) => WindowRegionHelper.HideFromAltTab(this);
    }

    /// <summary>Reparents this surface onto the desktop background layer and positions it over its monitor.</summary>
    public bool AttachToDesktop(bool clickThrough)
    {
        var ok = DesktopWallpaperHost.Attach(Handle, clickThrough);
        DesktopWallpaperHost.MoveToMonitor(Handle, ScreenPhysical);
        return ok;
    }

    /// <summary>Re-applies the window geometry over its monitor (after a display change or a background-host rebuild).</summary>
    public void RepositionOnMonitor(Drawing.Rectangle screenPhysical)
    {
        ScreenPhysical = screenPhysical;
        DesktopWallpaperHost.MoveToMonitor(Handle, ScreenPhysical);
    }

    /// <summary>Switches the displayed wallpaper; all kinds are rendered by the web runtime.</summary>
    public void SetWallpaper(WallpaperItem? item, double globalVolume)
    {
        this.globalVolume = globalVolume;
        current = item;
        try
        {
            var missing = item == null || (string.IsNullOrWhiteSpace(item.Path) && item.Kind != WallpaperKind.Shader)
                || (item.Kind is WallpaperKind.Video or WallpaperKind.Image && !File.Exists(item.Path));
            if (item == null || missing)
            {
                ClearContent();
                return;
            }
            _ = ShowWebAsync(item);
        }
        catch { ClearContent(); }
    }

    // ---- web runtime (WebView2) ----

    async Task ShowWebAsync(WallpaperItem item)
    {
        try
        {
            if (web == null)
            {
                web = new Microsoft.Web.WebView2.Wpf.WebView2 { DefaultBackgroundColor = Drawing.Color.Black };
                root.Children.Add(web);
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(BeeXPaths.DataDir, "WallpaperWV2"));
                await web.EnsureCoreWebView2Async(env);
                var core = web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                var assets = await Task.Run(WallpaperWebAssets.EnsureWebAssets);
                core.SetVirtualHostNameToFolderMapping("wallpaper.beex", assets, CoreWebView2HostResourceAccessKind.Allow);
                // Inject the SDK into every document so imported web wallpapers (incl. WE-HTML) get the shim too.
                try { await core.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(Path.Combine(assets, "runtime.js"))); } catch { }
                bridge.Attach(core);
                bridge.Ready += () => Dispatcher.BeginInvoke(() =>
                {
                    bridge.PostMonitor(ScreenPhysical.Width, ScreenPhysical.Height, 1.0);
                    bridge.PostFps(targetFps);
                    lastPostedVolume = EffectiveVolume();
                    bridge.PostVolume(lastPostedVolume);
                    bridge.PostMute(muted);
                    if (current?.Props is { Count: > 0 } props) bridge.PostProps(props);
                });
            }
            if (current != item) return; // switched away while initialising
            web.Visibility = Visibility.Visible;
            var core2 = web.CoreWebView2;
            // Per-item virtual host: the wallpaper's own folder (web assets, scene.json + media).
            var itemDir = ResolveItemDir(item);
            if (itemDir != null)
                core2.SetVirtualHostNameToFolderMapping("item.beex", itemDir, CoreWebView2HostResourceAccessKind.Allow);
            core2.Navigate(ResolveUrl(item));
        }
        catch { }
    }

    static string? ResolveItemDir(WallpaperItem item)
    {
        try
        {
            if (item.Path.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase)) return null;
            var dir = Path.GetDirectoryName(item.Path);
            return dir != null && Directory.Exists(dir) ? dir : null;
        }
        catch { return null; }
    }

    static string ResolveUrl(WallpaperItem item)
    {
        if (item.Kind == WallpaperKind.Scene) return "https://wallpaper.beex/scene.html?scene=https://item.beex/scene.json";
        if (item.Path.Equals("builtin:particles", StringComparison.OrdinalIgnoreCase)) return "https://wallpaper.beex/builtin/particles.html";
        if (item.Path.StartsWith("builtin:", StringComparison.OrdinalIgnoreCase) || item.Kind == WallpaperKind.Shader) return "https://wallpaper.beex/builtin/shader.html";
        if (item.Kind is WallpaperKind.Image or WallpaperKind.Video)
        {
            // Imported media plays through media.html (fullscreen img / looping video element).
            var mediaUrl = "https://item.beex/" + Uri.EscapeDataString(Path.GetFileName(item.Path));
            var kind = item.Kind == WallpaperKind.Video ? "video" : "image";
            var rate = (item.PlaybackRate <= 0 ? 1 : item.PlaybackRate).ToString("0.###", CultureInfo.InvariantCulture);
            return $"https://wallpaper.beex/media.html?kind={kind}&src={Uri.EscapeDataString(mediaUrl)}&rate={rate}";
        }
        return "https://item.beex/" + Uri.EscapeDataString(Path.GetFileName(item.Path));
    }

    void HideWeb()
    {
        if (web == null) return;
        try { bridge.PostPause(); } catch { }
        web.Visibility = Visibility.Collapsed;
        try { web.CoreWebView2?.Navigate("about:blank"); } catch { }
    }

    /// <summary>Latest audio spectrum for audio-reactive web wallpapers.</summary>
    public void OnAudio(float[] bands, bool beat, float level)
    {
        if (!IsWebSurface || current?.AudioReactive != true || targetFps <= 0) return;
        bridge.PostAudio(bands, beat, level);
    }

    /// <summary>Global pointer sample in screen pixels; forwarded normalised to this monitor when interactive.</summary>
    public void OnPointer(int screenX, int screenY, bool down)
    {
        if (!IsWebSurface || current?.Interactive != true || targetFps <= 0) return;
        var m = ScreenPhysical;
        if (m.Width <= 0 || m.Height <= 0) return;
        var x = Math.Clamp((screenX - m.Left) / (double)m.Width, 0, 1);
        var y = Math.Clamp((screenY - m.Top) / (double)m.Height, 0, 1);
        bridge.PostPointer(x, y, down);
    }

    /// <summary>Applies the governor's target frame rate: 0 pauses playback (suspending the browser), positive resumes.</summary>
    public void SetTargetFps(int fps)
    {
        var previous = targetFps;
        targetFps = fps;
        if (!IsWebSurface || web?.CoreWebView2 == null) return;
        try
        {
            if (fps <= 0 && previous > 0)
            {
                bridge.PostPause();
                // TrySuspendAsync needs a hidden browser; the surface is covered anyway when fps hits 0.
                web.Visibility = Visibility.Hidden;
                webSuspended = true;
                _ = web.CoreWebView2.TrySuspendAsync();
            }
            else if (fps > 0)
            {
                if (webSuspended)
                {
                    webSuspended = false;
                    try { web.CoreWebView2.Resume(); } catch { }
                    web.Visibility = Visibility.Visible;
                    bridge.PostResume();
                }
                bridge.PostFps(fps);
            }
        }
        catch { }
    }

    double EffectiveVolume() => Math.Clamp((current?.Volume ?? 0) * Math.Clamp(globalVolume, 0, 1), 0, 1);

    /// <summary>Updates the master volume applied on top of the per-item volume.</summary>
    public void SetGlobalVolume(double volume)
    {
        globalVolume = volume;
        ApplyVolume();
    }

    /// <summary>Mutes or restores the wallpaper audio (used when a fullscreen app takes over).</summary>
    public void SetMuted(bool value)
    {
        if (muted == value) return;
        muted = value;
        try { bridge.PostMute(value); } catch { }
    }

    void ApplyVolume()
    {
        var effective = EffectiveVolume();
        if (Math.Abs(effective - lastPostedVolume) < 0.0001) return;
        lastPostedVolume = effective;
        try { bridge.PostVolume(effective); } catch { }
    }

    void ClearContent()
    {
        HideWeb();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { DesktopWallpaperHost.Detach(Handle); } catch { }
        try { web?.Dispose(); } catch { }
        web = null;
        base.OnClosed(e);
    }
}
