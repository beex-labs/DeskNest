using System.Drawing;
using System.Runtime.InteropServices;

namespace BeeX.DeskNest;

/// <summary>
/// Places a render window on the desktop background layer, beneath the desktop icons, and positions it over a
/// specific monitor. It asks the shell to create the background host window, reparents the render window under it,
/// and applies extended styles so the surface stays out of Alt-Tab, never steals focus, and (by default) lets mouse
/// input fall through to the real desktop. All members are static and free of UI dependencies.
/// </summary>
static class DesktopWallpaperHost
{
    const int GwlStyle = -16;
    const int GwlExStyle = -20;
    const long WsChild = 0x40000000L;
    const long WsPopup = 0x80000000L;
    const long WsExTransparent = 0x00000020L;
    const long WsExToolWindow = 0x00000080L;
    const long WsExAppWindow = 0x00040000L;
    const long WsExNoActivate = 0x08000000L;
    const uint SmtoNormal = 0x0000;
    const int SmXVirtualScreen = 76;
    const int SmYVirtualScreen = 77;
    const uint SwpNoActivate = 0x0010;
    const uint SwpNoZOrder = 0x0004;
    const uint SwpNoMove = 0x0002;
    const uint SwpNoSize = 0x0001;
    const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string? className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr newParent);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    /// <summary>
    /// Asks the shell to spawn the background host window that lives between the wallpaper and the desktop icons, then
    /// returns it. Falls back to the desktop root window when a dedicated host is not exposed (e.g. single monitor).
    /// </summary>
    public static IntPtr EnsureBackgroundHost()
    {
        var progman = FindWindow("Progman", null);
        if (progman == IntPtr.Zero) return IntPtr.Zero;
        // Undocumented request that makes the shell split off a background host window behind the icon layer.
        SendMessageTimeout(progman, 0x052C, (IntPtr)0xD, (IntPtr)0x1, SmtoNormal, 1000, out _);

        // Pre-24H2: the spawned background host is a TOP-LEVEL WorkerW sibling that follows the window owning the
        // icon view (SHELLDLL_DefView); a surface parented under it shows through the transparent icon view.
        var host = IntPtr.Zero;
        EnumWindows((top, _) =>
        {
            if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                host = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);
        if (host != IntPtr.Zero && IsWindowVisible(host)) return host;

        // Win11 24H2+: the icon view is a direct child of Progman and the spawned WorkerW is only a hidden helper
        // child of Progman — a surface parented under it never becomes visible. The visible background band is
        // Progman itself: a child of Progman renders below the icon view and above the static wallpaper.
        return progman;
    }

    /// <summary>
    /// Reparents the render window onto the background host and applies the background-surface extended styles.
    /// When clickThrough is true the surface passes all mouse input to the desktop below it.
    /// </summary>
    public static bool Attach(IntPtr renderWindow, bool clickThrough)
    {
        if (renderWindow == IntPtr.Zero) return false;
        var host = EnsureBackgroundHost();
        if (host == IntPtr.Zero) return false;
        ApplyBackgroundStyles(renderWindow, clickThrough);
        // MSDN: SetParent does not modify WS_CHILD/WS_POPUP. An overlapped top-level window reparented to a
        // non-desktop window silently keeps its top-level state (GetParent returns 0) and never lands on the
        // desktop layer, so convert it to a proper child window BEFORE reparenting.
        var style = GetWindowLongPtr(renderWindow, GwlStyle).ToInt64();
        style = (style & ~WsPopup) | WsChild;
        SetWindowLongPtr(renderWindow, GwlStyle, new IntPtr(style));
        if (SetParent(renderWindow, host) == IntPtr.Zero) return false;
        // When hosted on Progman (24H2), keep the surface directly below the icon view so desktop icons stay on top.
        var defView = FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
            SetWindowPos(renderWindow, defView, 0, 0, 0, 0, SwpNoActivate | SwpNoMove | SwpNoSize);
        return true;
    }

    /// <summary>Detaches the render window from the background host so it becomes an ordinary top-level window again.</summary>
    public static void Detach(IntPtr renderWindow)
    {
        if (renderWindow != IntPtr.Zero) SetParent(renderWindow, IntPtr.Zero);
    }

    /// <summary>True when the previously resolved background host handle is still a live window.</summary>
    public static bool IsAlive(IntPtr host) => host != IntPtr.Zero && IsWindow(host);

    /// <summary>
    /// Positions the render window over the target monitor. The background host spans the whole virtual desktop, so a
    /// child at (0,0) maps to the virtual-screen origin; screen coordinates are offset by that origin accordingly.
    /// </summary>
    public static void MoveToMonitor(IntPtr renderWindow, Rectangle monitorPhysical)
    {
        if (renderWindow == IntPtr.Zero) return;
        var originX = GetSystemMetrics(SmXVirtualScreen);
        var originY = GetSystemMetrics(SmYVirtualScreen);
        SetWindowPos(renderWindow, IntPtr.Zero, monitorPhysical.Left - originX, monitorPhysical.Top - originY,
            monitorPhysical.Width, monitorPhysical.Height, SwpNoActivate | SwpNoZOrder | SwpShowWindow);
    }

    /// <summary>Toggles whether the surface lets mouse input fall through to the desktop (used to switch interactive mode).</summary>
    public static void SetClickThrough(IntPtr renderWindow, bool clickThrough)
    {
        if (renderWindow == IntPtr.Zero) return;
        var style = GetWindowLongPtr(renderWindow, GwlExStyle).ToInt64();
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(renderWindow, GwlExStyle, new IntPtr(style));
    }

    static void ApplyBackgroundStyles(IntPtr renderWindow, bool clickThrough)
    {
        var style = GetWindowLongPtr(renderWindow, GwlExStyle).ToInt64();
        style |= WsExNoActivate | WsExToolWindow;
        style &= ~WsExAppWindow;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(renderWindow, GwlExStyle, new IntPtr(style));
    }
}
