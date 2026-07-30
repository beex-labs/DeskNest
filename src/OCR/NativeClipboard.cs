using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BeeX.OCR;

internal static class NativeClipboard
{
    private const uint CfUnicodeText = 13;
    private const uint GmemMoveable = 0x0002;

    public static void SetText(string text)
    {
        SetText(text, attempts: 40, delayMilliseconds: 75);
    }

    public static bool TrySetText(string text, int attempts, int delayMilliseconds, out string? errorMessage)
    {
        try
        {
            SetText(text, attempts, delayMilliseconds);
            errorMessage = null;
            return true;
        }
        catch (InvalidOperationException ex)
        {
            errorMessage = ex.Message;
            return false;
        }
    }

    private static void SetText(string text, int attempts, int delayMilliseconds)
    {
        Exception? lastError = null;

        for (int attempt = 0; attempt < Math.Max(1, attempts); attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                lastError = new Win32Exception(Marshal.GetLastWin32Error());
                Thread.Sleep(Math.Max(1, delayMilliseconds));
                continue;
            }

            IntPtr handle = IntPtr.Zero;

            try
            {
                if (!EmptyClipboard())
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                byte[] bytes = Encoding.Unicode.GetBytes(text + '\0');
                handle = GlobalAlloc(GmemMoveable, (UIntPtr)bytes.Length);
                if (handle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                IntPtr target = GlobalLock(handle);
                if (target == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                try
                {
                    Marshal.Copy(bytes, 0, target, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                handle = IntPtr.Zero;
                return;
            }
            catch (Exception ex) when (ex is Win32Exception or ExternalException)
            {
                lastError = ex;
                Thread.Sleep(Math.Max(1, delayMilliseconds));
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    GlobalFree(handle);
                }

                CloseClipboard();
            }
        }

        throw new InvalidOperationException("剪贴板正忙，复制失败。请稍后手动复制识别结果。", lastError);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr newOwner);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalLock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
