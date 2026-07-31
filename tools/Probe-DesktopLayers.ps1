# 一次性诊断脚本：查看 24H2 下桌面窗口层级（Progman / WorkerW / SHELLDLL_DefView）
$src = @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class Probe {
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowW(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowExW(IntPtr p, IntPtr a, string c, IntPtr w);
  [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeoutW(IntPtr h, uint m, IntPtr wp, IntPtr lp, uint f, uint t, out IntPtr r);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  public struct RECT { public int L, T, R, B; }
  public delegate bool EnumProc(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr p, EnumProc cb, IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
}
'@
Add-Type -TypeDefinition $src

$progman = [Probe]::FindWindowW('Progman', [IntPtr]::Zero)
"Progman = 0x{0:X} ({0})" -f $progman.ToInt64()
[Probe]::SendMessageTimeoutW($progman, 0x052C, [IntPtr]0xD, [IntPtr]0x1, 0, 1000, [ref]([IntPtr]::Zero)) | Out-Null
Start-Sleep -Milliseconds 500

"`n--- Progman descendants (EnumChildWindows) ---"
$cbc = [Probe+EnumProc]{ param($h, $l)
  $r = New-Object Probe+RECT
  [Probe]::GetWindowRect($h, [ref]$r) | Out-Null
  $p = [Probe]::GetParent($h)
  "  0x{0:X}  {1,-20} parent=0x{2:X} vis={3}  rect=({4},{5})-({6},{7})" -f $h.ToInt64(), [Probe]::Cls($h), $p.ToInt64(), [Probe]::IsWindowVisible($h), $r.L, $r.T, $r.R, $r.B | Out-Host
  return $true
}
[Probe]::EnumChildWindows($progman, $cbc, [IntPtr]::Zero) | Out-Null

"`n--- top-level WorkerW windows ---"
$cb = [Probe+EnumProc]{ param($h, $l)
  if ([Probe]::Cls($h) -eq 'WorkerW') {
    $r = New-Object Probe+RECT
    [Probe]::GetWindowRect($h, [ref]$r) | Out-Null
    "  0x{0:X} vis={1} rect=({2},{3})-({4},{5})" -f $h.ToInt64(), [Probe]::IsWindowVisible($h), $r.L, $r.T, $r.R, $r.B | Out-Host
  }
  return $true
}
[Probe]::EnumWindows($cb, [IntPtr]::Zero) | Out-Null
