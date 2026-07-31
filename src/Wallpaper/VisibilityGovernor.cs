using System.Drawing;
using System.Runtime.InteropServices;

namespace BeeX.DeskNest;

/// <summary>
/// Decides, per monitor, how much of the desktop wallpaper is actually visible and what frame rate to run at.
/// The visibility math and the frame-rate decision are pure functions (unit-testable); the Win32 helpers below
/// collect the live occluder rectangles, fullscreen state and power status that feed those functions.
/// </summary>
static class VisibilityGovernor
{
    // ---- pure visibility math ----

    /// <summary>Area of the monitor rectangle left uncovered after removing every occluder (occluders are clipped to the monitor first).</summary>
    public static long UncoveredArea(Rectangle monitor, IReadOnlyList<Rectangle> occluders)
    {
        if (monitor.Width <= 0 || monitor.Height <= 0) return 0;
        var free = new List<Rectangle> { monitor };
        foreach (var raw in occluders)
        {
            var occ = Rectangle.Intersect(raw, monitor);
            if (occ.IsEmpty) continue;
            var next = new List<Rectangle>(free.Count + 3);
            foreach (var f in free) SubtractInto(f, occ, next);
            free = next;
            if (free.Count == 0) break;
        }
        long area = 0;
        foreach (var f in free) area += (long)f.Width * f.Height;
        return area;
    }

    /// <summary>Fraction of the monitor still visible: 0 means fully covered, 1 means nothing is on top of the wallpaper.</summary>
    public static double VisibleFraction(Rectangle monitor, IReadOnlyList<Rectangle> occluders)
    {
        long total = (long)monitor.Width * monitor.Height;
        if (total <= 0) return 0;
        return Math.Clamp((double)UncoveredArea(monitor, occluders) / total, 0, 1);
    }

    // Splits the free rectangle into the disjoint pieces that remain after removing occ, appending them to output.
    static void SubtractInto(Rectangle f, Rectangle occ, List<Rectangle> output)
    {
        var o = Rectangle.Intersect(f, occ);
        if (o.IsEmpty) { output.Add(f); return; }
        if (o.Top > f.Top) output.Add(new Rectangle(f.Left, f.Top, f.Width, o.Top - f.Top));
        if (o.Bottom < f.Bottom) output.Add(new Rectangle(f.Left, o.Bottom, f.Width, f.Bottom - o.Bottom));
        if (o.Left > f.Left) output.Add(new Rectangle(f.Left, o.Top, o.Left - f.Left, o.Height));
        if (o.Right < f.Right) output.Add(new Rectangle(o.Right, o.Top, f.Right - o.Right, o.Height));
    }

    /// <summary>
    /// Target frame rate for one monitor given its visible fraction, the foreground/power conditions and user
    /// preferences. Returns 0 to fully pause. A fullscreen foreground app, battery-saver mode, being on battery
    /// (when configured), or being completely covered (when configured) all pause rendering.
    /// </summary>
    public static int TargetFps(double visibleFraction, bool fullscreen, bool onBattery, bool batterySaver,
        bool pauseWhenOccluded, bool pauseOnBattery, int fpsCap, int refreshHz)
    {
        if (fullscreen) return 0;
        if (batterySaver) return 0;
        if (onBattery && pauseOnBattery) return 0;
        if (pauseWhenOccluded && visibleFraction <= 0.01) return 0;
        var refresh = refreshHz > 0 ? refreshHz : 60;
        return Math.Clamp(Math.Min(fpsCap, refresh), 1, 240);
    }

    // ---- live Win32 data ----

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, char[] buffer, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    const int GwlExStyle = -20;
    const long WsExToolWindow = 0x00000080L;
    const int DwmwaCloaked = 14;

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

    /// <summary>True when the device is currently running on battery (AC offline).</summary>
    public static bool OnBattery() => GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;

    /// <summary>True when Windows battery-saver mode is active.</summary>
    public static bool BatterySaver() => GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1;

    /// <summary>Collects the physical-pixel rectangles of top-level windows that sit on top of the wallpaper on the given monitor.</summary>
    public static List<Rectangle> CollectOccluders(Rectangle monitor)
    {
        var result = new List<Rectangle>();
        EnumWindows((hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                if ((GetWindowLong(hWnd, GwlExStyle) & (int)WsExToolWindow) != 0) return true;
                if (IsShellWindow(hWnd) || IsCloaked(hWnd)) return true;
                if (!GetWindowRect(hWnd, out var r)) return true;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (rect.Width <= 0 || rect.Height <= 0) return true;
                var hit = Rectangle.Intersect(rect, monitor);
                if (!hit.IsEmpty) result.Add(hit);
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>True when the foreground window fully covers the given monitor (a fullscreen app or video).</summary>
    public static bool IsForegroundFullscreen(Rectangle monitor)
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || IsShellWindow(fg)) return false;
        if (!GetWindowRect(fg, out var r)) return false;
        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    static bool IsCloaked(IntPtr hWnd)
    {
        try { return DwmGetWindowAttribute(hWnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; }
    }

    static bool IsShellWindow(IntPtr hWnd)
    {
        var buffer = new char[64];
        var len = GetClassName(hWnd, buffer, buffer.Length);
        if (len <= 0) return false;
        var name = new string(buffer, 0, len);
        return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "SysListView32";
    }
}
