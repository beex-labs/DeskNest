using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Brushes = System.Windows.Media.Brushes;
using Stretch = System.Windows.Media.Stretch;
using Drawing = System.Drawing;

namespace BeeX.DeskNest;

/// <summary>
/// A borderless render surface for one monitor that is hosted on the desktop background layer. It shows a video
/// (looped, hardware-decoded) or a still image; other wallpaper kinds are reserved for the web runtime. The frame
/// rate is driven externally by the governor: a target of 0 pauses playback to save power.
/// </summary>
public sealed class WallpaperWindow : Window
{
    readonly MediaElement media = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Manual, Stretch = Stretch.UniformToFill, ScrubbingEnabled = false };
    readonly System.Windows.Controls.Image image = new() { Stretch = Stretch.UniformToFill };
    WallpaperItem? current;
    double globalVolume;
    int targetFps = 60;

    public string DeviceName { get; }
    public Drawing.Rectangle ScreenPhysical { get; private set; }
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

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
        media.MediaEnded += (_, _) => { try { media.Position = TimeSpan.Zero; media.Play(); } catch { } };
        media.MediaFailed += (_, _) => { };
        var root = new Grid { ClipToBounds = true };
        root.Children.Add(image);
        root.Children.Add(media);
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

    /// <summary>Switches the displayed wallpaper. Only video and image kinds are rendered here; unsupported kinds clear the surface.</summary>
    public void SetWallpaper(WallpaperItem? item, double globalVolume)
    {
        this.globalVolume = globalVolume;
        current = item;
        try
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
            {
                ClearContent();
                return;
            }
            if (item.Kind == WallpaperKind.Video)
            {
                image.Source = null;
                image.Visibility = Visibility.Collapsed;
                media.Visibility = Visibility.Visible;
                media.SpeedRatio = Math.Clamp(item.PlaybackRate <= 0 ? 1 : item.PlaybackRate, 0.25, 4);
                media.Source = new Uri(item.Path);
                ApplyVolume();
                if (targetFps > 0) media.Play();
            }
            else if (item.Kind == WallpaperKind.Image)
            {
                media.Stop();
                media.Source = null;
                media.Visibility = Visibility.Collapsed;
                image.Source = LoadImage(item.Path);
                image.Visibility = Visibility.Visible;
            }
            else
            {
                // Web/Shader/Scene surfaces are handled by the web runtime added in a later stage.
                ClearContent();
            }
        }
        catch { ClearContent(); }
    }

    /// <summary>Applies the governor's target frame rate: 0 pauses video playback, any positive value resumes it.</summary>
    public void SetTargetFps(int fps)
    {
        targetFps = fps;
        if (current?.Kind != WallpaperKind.Video) return;
        try
        {
            if (fps <= 0) media.Pause();
            else media.Play();
        }
        catch { }
    }

    /// <summary>Updates the master volume applied on top of the per-item volume.</summary>
    public void SetGlobalVolume(double volume)
    {
        globalVolume = volume;
        ApplyVolume();
    }

    /// <summary>Mutes or restores the wallpaper audio (used when a fullscreen app takes over).</summary>
    public void SetMuted(bool muted)
    {
        try { media.IsMuted = muted; } catch { }
    }

    void ApplyVolume()
    {
        var itemVolume = current?.Volume ?? 0;
        try { media.Volume = Math.Clamp(itemVolume * Math.Clamp(globalVolume, 0, 1), 0, 1); } catch { }
    }

    void ClearContent()
    {
        try { media.Stop(); } catch { }
        media.Source = null;
        media.Visibility = Visibility.Collapsed;
        image.Source = null;
        image.Visibility = Visibility.Collapsed;
    }

    static BitmapImage LoadImage(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    protected override void OnClosed(EventArgs e)
    {
        try { DesktopWallpaperHost.Detach(Handle); } catch { }
        try { media.Stop(); media.Close(); } catch { }
        base.OnClosed(e);
    }
}
