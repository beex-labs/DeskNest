using System.Runtime.InteropServices;

namespace BeeX.DeskNest;

/// <summary>
/// Observe-only global pointer feed for interactive wallpapers. A WH_MOUSE_LL hook watches cursor movement and left
/// button state without consuming any event (always CallNextHookEx), so desktop icons and context menus keep working
/// while the wallpaper receives the same pointer stream. Install/uninstall on the UI thread (needs a message pump).
/// </summary>
sealed class PointerForwarder : IDisposable
{
    const int WhMouseLl = 14;
    const int WmMouseMove = 0x0200;
    const int WmLButtonDown = 0x0201;
    const int WmLButtonUp = 0x0202;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] static extern IntPtr SetWindowsHookExW(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);

    [StructLayout(LayoutKind.Sequential)]
    struct MsllHookStruct { public int X; public int Y; public uint MouseData, Flags, Time; public IntPtr ExtraInfo; }

    readonly HookProc proc; // kept alive for the native hook's lifetime
    IntPtr hook;
    bool down;

    /// <summary>Screen-pixel position and current left-button state; raised on the hooking (UI) thread.</summary>
    public event Action<int, int, bool>? PointerChanged;

    public PointerForwarder()
    {
        proc = OnHook;
        hook = SetWindowsHookExW(WhMouseLl, proc, GetModuleHandle(null), 0);
    }

    IntPtr OnHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var msg = wParam.ToInt64();
                if (msg is WmMouseMove or WmLButtonDown or WmLButtonUp)
                {
                    var data = Marshal.PtrToStructure<MsllHookStruct>(lParam);
                    if (msg == WmLButtonDown) down = true;
                    else if (msg == WmLButtonUp) down = false;
                    PointerChanged?.Invoke(data.X, data.Y, down);
                }
            }
            catch { }
        }
        return CallNextHookEx(hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { try { UnhookWindowsHookEx(hook); } catch { } hook = IntPtr.Zero; }
    }
}
