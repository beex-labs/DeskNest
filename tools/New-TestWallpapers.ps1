# 生成动态壁纸引擎（阶段一）手动测试用的素材：测试图卡 PNG + 颜色循环 MP4。
# 全程离线：图片用 System.Drawing，视频用系统自带 MediaFoundation（Windows.Media.Editing），无需 ffmpeg。
# 用法：powershell -ExecutionPolicy Bypass -File tools\New-TestWallpapers.ps1 [-OutDir D:\BeeX\TestWallpapers]
param([string]$OutDir = 'D:\BeeX\TestWallpapers')

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# ---------- 图片：带角标/网格/中心标签的测试图卡，便于核对多屏对位与 UniformToFill 裁切 ----------
Add-Type -AssemblyName System.Drawing

function New-TestImage {
    param([string]$Path, [string]$Label, [int]$W, [int]$H,
          [System.Drawing.Color]$Bg1, [System.Drawing.Color]$Bg2, [System.Drawing.Color]$Accent)
    $bmp = New-Object System.Drawing.Bitmap $W, $H
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.TextRenderingHint = 'AntiAliasGridFit'
    $rect = New-Object System.Drawing.Rectangle 0, 0, $W, $H
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush $rect, $Bg1, $Bg2, 45.0
    $g.FillRectangle($grad, $rect)

    # 网格（每 120px），用于观察缩放/裁切
    $gridPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(46, 255, 255, 255)), 1
    for ($x = 120; $x -lt $W; $x += 120) { $g.DrawLine($gridPen, $x, 0, $x, $H) }
    for ($y = 120; $y -lt $H; $y += 120) { $g.DrawLine($gridPen, 0, $y, $W, $y) }

    # 四角三角标 + 边框：任一角被裁掉即说明 UniformToFill 裁切了该方向
    $accentBrush = New-Object System.Drawing.SolidBrush $Accent
    $s = 90
    $g.FillPolygon($accentBrush, [System.Drawing.Point[]]@( (New-Object System.Drawing.Point 0,0),        (New-Object System.Drawing.Point $s,0),        (New-Object System.Drawing.Point 0,$s) ))
    $g.FillPolygon($accentBrush, [System.Drawing.Point[]]@( (New-Object System.Drawing.Point $W,0),       (New-Object System.Drawing.Point ($W-$s),0),   (New-Object System.Drawing.Point $W,$s) ))
    $g.FillPolygon($accentBrush, [System.Drawing.Point[]]@( (New-Object System.Drawing.Point 0,$H),       (New-Object System.Drawing.Point $s,$H),       (New-Object System.Drawing.Point 0,($H-$s)) ))
    $g.FillPolygon($accentBrush, [System.Drawing.Point[]]@( (New-Object System.Drawing.Point $W,$H),      (New-Object System.Drawing.Point ($W-$s),$H),  (New-Object System.Drawing.Point $W,($H-$s)) ))
    $borderPen = New-Object System.Drawing.Pen $Accent, 6
    $g.DrawRectangle($borderPen, 3, 3, ($W - 6), ($H - 6))

    # 中心十字 + 标签
    $crossPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(140, 255, 255, 255)), 2
    $g.DrawLine($crossPen, ($W/2 - 60), ($H/2), ($W/2 + 60), ($H/2))
    $g.DrawLine($crossPen, ($W/2), ($H/2 - 60), ($W/2), ($H/2 + 60))
    $fmt = New-Object System.Drawing.StringFormat
    $fmt.Alignment = 'Center'; $fmt.LineAlignment = 'Center'
    $fontBig = New-Object System.Drawing.Font 'Microsoft YaHei UI', ([int]($H/12)), ([System.Drawing.FontStyle]::Bold)
    $fontSub = New-Object System.Drawing.Font 'Microsoft YaHei UI', ([int]($H/32))
    $white = [System.Drawing.Brushes]::White
    $g.DrawString($Label, $fontBig, $white, (New-Object System.Drawing.RectangleF 0, ($H*0.30), $W, ($H*0.25)), $fmt)
    $g.DrawString("$W x $H", $fontSub, $white, (New-Object System.Drawing.RectangleF 0, ($H*0.55), $W, ($H*0.15)), $fmt)
    $g.Dispose()
    $bmp.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "  image  -> $Path"
}

$c = { param($r,$g,$b) [System.Drawing.Color]::FromArgb(255,$r,$g,$b) }
New-TestImage -Path (Join-Path $OutDir 'TestImage_A_1080p.png') -Label 'BeeX 壁纸测试 A' -W 1920 -H 1080 `
    -Bg1 (& $c 24 16 48)  -Bg2 (& $c 96 32 128)  -Accent (& $c 255 138 0)
New-TestImage -Path (Join-Path $OutDir 'TestImage_B_1080p.png') -Label 'BeeX 壁纸测试 B' -W 1920 -H 1080 `
    -Bg1 (& $c 8 40 48)   -Bg2 (& $c 0 96 104)   -Accent (& $c 0 230 190)
New-TestImage -Path (Join-Path $OutDir 'TestImage_C_4K.png')    -Label 'BeeX 壁纸测试 C (4K)' -W 3840 -H 2160 `
    -Bg1 (& $c 40 12 12)  -Bg2 (& $c 128 48 24)  -Accent (& $c 255 210 60)

# ---------- 视频：由 tools\TestWallpaperGen 生成（PS 5.1 的 WinRT 投影无法操作 IVector<MediaClip>，改用 dotnet 工具） ----------
dotnet run --project (Join-Path $PSScriptRoot 'TestWallpaperGen\TestWallpaperGen.csproj') -- $OutDir
if ($LASTEXITCODE -ne 0) { throw "TestWallpaperGen failed: $LASTEXITCODE" }

Write-Host "Done. Output: $OutDir"
