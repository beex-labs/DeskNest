using System.Runtime.InteropServices;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace BeeX.DeskNest;

/// <summary>
/// Owns the live wallpaper feature end to end: it creates one render surface per monitor, assigns each its wallpaper,
/// hosts them on the desktop background layer, and runs a periodic governor that adapts the frame rate to visibility
/// and power state. It also runs the shared audio-spectrum bus and the observe-only pointer feed for audio-reactive /
/// interactive web wallpapers. It rebuilds surfaces when the display layout changes, pauses on lock, and re-attaches
/// when the shell restarts.
/// </summary>
public sealed class WallpaperService : IDisposable
{
    readonly DeskNestService service;
    readonly Dictionary<string, WallpaperWindow> windows = new(StringComparer.OrdinalIgnoreCase);
    readonly DispatcherTimer governor = new() { Interval = TimeSpan.FromMilliseconds(500) };
    ReattachWatcher? reattach;
    AudioSpectrumBus? audioBus;
    PointerForwarder? pointer;
    long lastPointerMoveTicks;
    bool running;
    bool locked;

    AppState State => service.State;

    public WallpaperService(DeskNestService service) => this.service = service;

    public bool IsRunning => running;

    /// <summary>Starts the engine: builds per-monitor surfaces, subscribes to system events and begins the governor loop.</summary>
    public void Start()
    {
        if (running) return;
        running = true;
        SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        reattach ??= new ReattachWatcher(ReattachAll);
        governor.Tick += Governor_Tick;
        BuildWindows();
        governor.Start();
    }

    /// <summary>Stops the engine, tears down every surface and detaches from all system events.</summary>
    public void Stop()
    {
        if (!running) return;
        running = false;
        governor.Stop();
        governor.Tick -= Governor_Tick;
        SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        CloseAllWindows();
        StopAux();
    }

    /// <summary>Reconciles runtime state with settings: turns the engine on/off and refreshes assignments and volume.</summary>
    public void ApplyPreferences()
    {
        if (State.WallpaperEnabled && !running) { Start(); return; }
        if (!State.WallpaperEnabled && running) { Stop(); return; }
        if (!running) return;
        RebuildWindows();
    }

    /// <summary>Opens the wallpaper gallery where wallpapers are imported and assigned to monitors.</summary>
    public void ShowGallery()
    {
        var window = new WallpaperGalleryWindow(service);
        window.Show();
        window.Activate();
    }

    void BuildWindows()
    {
        foreach (var screen in Forms.Screen.AllScreens)
        {
            var item = ResolveWallpaper(screen.DeviceName);
            if (item == null) continue;
            var bounds = new Drawing.Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
            var window = new WallpaperWindow(screen.DeviceName, bounds);
            window.Show();
            // Surfaces always click through: interactive wallpapers observe the pointer via the low-level hook instead.
            window.AttachToDesktop(clickThrough: true);
            window.SetWallpaper(item, State.WallpaperGlobalVolume);
            windows[screen.DeviceName] = window;
        }
        UpdateAux();
    }

    void RebuildWindows()
    {
        CloseAllWindows();
        if (running) BuildWindows();
    }

    void CloseAllWindows()
    {
        foreach (var window in windows.Values)
            try { window.Close(); } catch { }
        windows.Clear();
    }

    // ---- shared audio / pointer feeds for web wallpapers ----

    // Starts or stops the spectrum bus and the pointer hook to match the surfaces currently on screen.
    void UpdateAux()
    {
        var needAudio = State.WallpaperAudioReactive && windows.Values.Any(w => w.IsWebSurface && w.Current?.AudioReactive == true);
        if (needAudio && audioBus == null)
        {
            audioBus = new AudioSpectrumBus();
            audioBus.SpectrumReady += OnSpectrum;
            audioBus.Start();
        }
        else if (!needAudio && audioBus != null)
        {
            audioBus.Dispose();
            audioBus = null;
        }

        var needPointer = windows.Values.Any(w => w.IsWebSurface && w.Current?.Interactive == true);
        if (needPointer && pointer == null)
        {
            pointer = new PointerForwarder();
            pointer.PointerChanged += OnPointer;
        }
        else if (!needPointer && pointer != null)
        {
            pointer.Dispose();
            pointer = null;
        }
    }

    void StopAux()
    {
        audioBus?.Dispose();
        audioBus = null;
        pointer?.Dispose();
        pointer = null;
    }

    // Capture thread → UI thread. The bands array is reused by the bus, so copy before leaving this frame.
    void OnSpectrum(float[] bands, bool beat, float level)
    {
        var copy = (float[])bands.Clone();
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var window in windows.Values)
                try { window.OnAudio(copy, beat, level); } catch { }
        }));
    }

    // Already on the UI thread (the hook runs on the dispatcher's message pump); throttle moves to ~60 Hz.
    void OnPointer(int x, int y, bool down)
    {
        var now = Environment.TickCount64;
        if (now - lastPointerMoveTicks < 15) return;
        lastPointerMoveTicks = now;
        foreach (var window in windows.Values)
            try { window.OnPointer(x, y, down); } catch { }
    }

    WallpaperItem? ResolveWallpaper(string deviceName)
    {
        if (!State.WallpaperPerMonitor.TryGetValue(deviceName, out var id)) return null;
        return State.WallpaperLibrary.FirstOrDefault(w => w.Id == id);
    }

    void Governor_Tick(object? sender, EventArgs e)
    {
        if (!running || locked) return;
        var cap = Math.Clamp(State.WallpaperFpsCap, 10, 240);
        var onBattery = VisibilityGovernor.OnBattery();
        var saver = VisibilityGovernor.BatterySaver();
        var anyActive = false;
        foreach (var window in windows.Values)
        {
            try
            {
                var monitor = window.ScreenPhysical;
                var fullscreen = VisibilityGovernor.IsForegroundFullscreen(monitor);
                var occluders = VisibilityGovernor.CollectOccluders(monitor);
                var visible = VisibilityGovernor.VisibleFraction(monitor, occluders);
                var fps = VisibilityGovernor.TargetFps(visible, fullscreen, onBattery, saver,
                    State.WallpaperPauseWhenOccluded, State.WallpaperPauseOnBattery, cap, cap);
                window.SetTargetFps(fps);
                window.SetGlobalVolume(State.WallpaperGlobalVolume);
                window.SetMuted(State.WallpaperMuteOnFullscreen && fullscreen);
                if (fps > 0) anyActive = true;
            }
            catch { }
        }
        // Skip FFT work entirely while every surface is paused.
        if (audioBus != null) audioBus.Muted = !anyActive;
    }

    void OnDisplayChanged(object? sender, EventArgs e)
    {
        if (running) System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(RebuildWindows));
    }

    void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock) { locked = true; foreach (var w in windows.Values) w.SetTargetFps(0); }
        else if (e.Reason == SessionSwitchReason.SessionUnlock) { locked = false; }
    }

    // Re-runs the background-host attach for every surface after the shell (Explorer) restarts and recreates it.
    void ReattachAll()
    {
        if (!running) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            foreach (var window in windows.Values)
            {
                try { window.AttachToDesktop(clickThrough: true); }
                catch { }
            }
        }));
    }

    public void Dispose()
    {
        Stop();
        StopAux();
        reattach?.Dispose();
        reattach = null;
    }

    // Hidden message-only window that observes the shell "TaskbarCreated" broadcast, which fires when Explorer restarts.
    sealed class ReattachWatcher : Forms.NativeWindow, IDisposable
    {
        readonly uint message;
        readonly Action onShellRestart;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
        public ReattachWatcher(Action onShellRestart)
        {
            this.onShellRestart = onShellRestart;
            message = RegisterWindowMessage("TaskbarCreated");
            CreateHandle(new Forms.CreateParams());
        }
        protected override void WndProc(ref Forms.Message m)
        {
            if (m.Msg == message) { try { onShellRestart(); } catch { } }
            base.WndProc(ref m);
        }
        public void Dispose() => DestroyHandle();
    }
}
