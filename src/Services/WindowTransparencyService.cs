using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace BeeX.DeskNest;

// 視窗透明化核心：以 WS_EX_LAYERED + SetLayeredWindowAttributes 調整任意視窗整體透明度，
// 記錄原始狀態以便還原；不注入 DLL、不修改目標程式。功能取自 BeeX_ClearWindow 並整合進 DeskNest。
public sealed class WindowTransparencyService
{
    sealed class WindowState
    {
        public IntPtr ExtendedStyle;
        public bool OriginallyLayered;
        public bool AttributesWereRead;
        public uint ColorKey;
        public byte Alpha;
        public uint Flags;
    }

    readonly Dictionary<IntPtr, WindowState> originalStates = [];

    public int ModifiedCount => originalStates.Count;
    public bool HasModifiedWindows => originalStates.Count > 0;

    public static IntPtr GetRootWindowUnderCursor()
    {
        if (!GetCursorPos(out var cursor)) throw new Win32Exception(Marshal.GetLastWin32Error(), "無法取得鼠標位置。");
        var hovered = WindowFromPoint(cursor);
        var root = GetAncestor(hovered, GA_ROOT);
        if (root == IntPtr.Zero || !IsWindow(root) || !IsWindowVisible(root)) throw new InvalidOperationException("沒有找到有效的目標視窗。");
        return root;
    }

    public void ApplyOpacity(IntPtr hwnd, byte alpha)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) throw new InvalidOperationException("目標視窗已失效，請重新選取視窗。");
        SaveOriginalState(hwnd);
        var current = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var layered = new IntPtr(current.ToInt64() | WS_EX_LAYERED);
        SetWindowLongPtrChecked(hwnd, GWL_EXSTYLE, layered);
        if (!SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒絕設置透明度。");
        RefreshWindow(hwnd);
    }

    public bool RestoreWindow(IntPtr hwnd)
    {
        if (!originalStates.TryGetValue(hwnd, out var state)) return false;
        try
        {
            if (!IsWindow(hwnd)) return false;
            SetWindowLongPtrChecked(hwnd, GWL_EXSTYLE, state.ExtendedStyle);
            if (state.OriginallyLayered && state.AttributesWereRead) SetLayeredWindowAttributes(hwnd, state.ColorKey, state.Alpha, state.Flags);
            RefreshWindow(hwnd);
            return true;
        }
        catch { return false; }
        finally { originalStates.Remove(hwnd); }
    }

    public int RestoreAllWindows()
    {
        var restored = 0;
        foreach (var hwnd in originalStates.Keys.ToArray()) if (RestoreWindow(hwnd)) restored++;
        return restored;
    }

    // 最小化所有已被透明化的視窗（供 Alt+X 全域快捷鍵調用），保留透明度記錄不還原。
    public int MinimizeAllTransparent()
    {
        var count = 0;
        foreach (var hwnd in originalStates.Keys.ToArray())
        {
            if (!IsWindow(hwnd)) { originalStates.Remove(hwnd); continue; }
            if (ShowWindow(hwnd, SW_MINIMIZE)) count++;
        }
        return count;
    }

    public static string GetWindowTitle(IntPtr hwnd)
    {
        var length = GetWindowTextLength(hwnd);
        var text = new StringBuilder(length + 1);
        GetWindowText(hwnd, text, text.Capacity);
        return text.ToString();
    }

    public static string GetProcessName(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch { return "未知程序"; }
    }

    void SaveOriginalState(IntPtr hwnd)
    {
        if (originalStates.ContainsKey(hwnd)) return;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var layered = (style.ToInt64() & WS_EX_LAYERED) != 0;
        uint colorKey = 0; byte alpha = 255; uint flags = 0; var read = false;
        if (layered) read = GetLayeredWindowAttributes(hwnd, out colorKey, out alpha, out flags);
        originalStates[hwnd] = new WindowState { ExtendedStyle = style, OriginallyLayered = layered, AttributesWereRead = read, ColorKey = colorKey, Alpha = alpha, Flags = flags };
    }

    static void RefreshWindow(IntPtr hwnd) => SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

    static IntPtr GetWindowLongPtr(IntPtr hwnd, int index) => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, index) : new IntPtr(GetWindowLong32(hwnd, index));
    static IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value) => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, index, value) : new IntPtr(SetWindowLong32(hwnd, index, value.ToInt32()));
    static void SetWindowLongPtrChecked(IntPtr hwnd, int index, IntPtr value)
    {
        SetLastError(0);
        var previous = SetWindowLongPtr(hwnd, index, value);
        var error = Marshal.GetLastWin32Error();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
    }

    const int GWL_EXSTYLE = -20;
    const long WS_EX_LAYERED = 0x00080000L;
    const uint LWA_ALPHA = 0x00000002;
    const uint GA_ROOT = 2;
    const int SW_MINIMIZE = 6;
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010, SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")][return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)] static extern int GetWindowLong32(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr64(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)] static extern int SetWindowLong32(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint colorKey, out byte alpha, out uint flags);
    [DllImport("user32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("kernel32.dll")] static extern void SetLastError(uint code);
}
