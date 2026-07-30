param(
    [Parameter(Mandatory=$true)]
    [string]$ExePath,

    [string]$OutputDir = "",

    [int]$StartupDelaySeconds = 5,

    [string[]]$SelectedWidgets = @(),

    [string[]]$SelectedVariants = @(),

    [string[]]$SelectedThemes = @()
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path -LiteralPath $ExePath)) {
    throw "ExePath not found: $ExePath"
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $PSScriptRoot "..\tmp\widget-regression\$stamp"
}
if ([System.IO.Path]::IsPathRooted($OutputDir)) {
    $OutputDir = [System.IO.Path]::GetFullPath($OutputDir)
} else {
    $OutputDir = [System.IO.Path]::GetFullPath((Join-Path (Get-Location) $OutputDir))
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$stateDir = Join-Path $env:LOCALAPPDATA "BeeX\DeskNest"
$statePath = Join-Path $stateDir "state.json"
$backupPath = Join-Path $OutputDir "state.backup.json"
$sampleFolder = Join-Path $OutputDir "sample-files"
$cityShenzhen = [string]::Concat([char]0x6DF1, [char]0x5733)
$hadState = Test-Path -LiteralPath $statePath
New-Item -ItemType Directory -Force -Path $stateDir | Out-Null
New-Item -ItemType Directory -Force -Path $sampleFolder | Out-Null
"Design brief sample" | Set-Content -LiteralPath (Join-Path $sampleFolder "Design brief.txt") -Encoding UTF8
"Roadmap sample" | Set-Content -LiteralPath (Join-Path $sampleFolder "Roadmap.md") -Encoding UTF8
New-Item -ItemType Directory -Force -Path (Join-Path $sampleFolder "Assets") | Out-Null
if ($hadState) {
    Copy-Item -LiteralPath $statePath -Destination $backupPath -Force
}

$screen = ([System.Windows.Forms.Screen]::AllScreens | Sort-Object { $_.Bounds.Left } | Select-Object -First 1).Bounds
$left = [Math]::Max($screen.Left + 80, 60)
$top = [Math]::Max($screen.Top + 80, 60)

$widgets = @(
    @{ Kind="Todo";          KindValue=1;  Title="Todo min";      Width=390; Height=300 },
    @{ Kind="Capture";       KindValue=4;  Title="Capture min";   Width=390; Height=260 },
    @{ Kind="Music";         KindValue=5;  Title="Music min";     Width=280; Height=220 },
    @{ Kind="Weather";       KindValue=8;  Title="Weather min";   Width=260; Height=190 },
    @{ Kind="Folder";        KindValue=2;  Title="Folder min";    Width=280; Height=180 },
    @{ Kind="ManagedFiles";  KindValue=3;  Title="Managed min";   Width=280; Height=180 },
    @{ Kind="Clock";         KindValue=6;  Title="Clock min";     Width=260; Height=200 },
    @{ Kind="Launcher";      KindValue=12; Title="Launcher min";  Width=360; Height=150 },
    @{ Kind="SystemMonitor"; KindValue=10; Title="Monitor min";   Width=320; Height=260 },
    @{ Kind="Tags";          KindValue=9;  Title="Tags min";      Width=280; Height=180 },
    @{ Kind="Countdown";     KindValue=11; Title="Countdown min"; Width=280; Height=180 },
    @{ Kind="WorkTimer";     KindValue=13; Title="Work min";      Width=300; Height=240 }
)

$variants = @(
    @{ Name="min";       ScaleW=1.00; ScaleH=1.00; Collapsed=$false },
    @{ Name="medium";    ScaleW=1.28; ScaleH=1.22; Collapsed=$false },
    @{ Name="wide";      ScaleW=1.75; ScaleH=1.08; Collapsed=$false },
    @{ Name="collapsed"; ScaleW=1.00; ScaleH=0.34; Collapsed=$true }
)

$themeScenarios = @(
    @{ Name="clear-zh-TW"; Theme="Acrylic"; ThemePreset="Clear"; Opacity=0.50; Text="#0D1321"; Language="zh-TW"; Corner=18 },
    @{ Name="dark-en-US"; Theme="Dark"; ThemePreset="Dark"; Opacity=0.62; Text="#FFFFFF"; Language="en-US"; Corner=18 },
    @{ Name="work-zh-CN"; Theme="Acrylic"; ThemePreset="Work"; Opacity=0.72; Text="#111827"; Language="zh-CN"; Corner=12 }
)

if ($SelectedWidgets.Count -gt 0) {
    $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $SelectedWidgets) { [void]$wanted.Add($item) }
    $widgets = @($widgets | Where-Object { $wanted.Contains($_.Kind) })
}
if ($SelectedVariants.Count -gt 0) {
    $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $SelectedVariants) { [void]$wanted.Add($item) }
    $variants = @($variants | Where-Object { $wanted.Contains($_.Name) })
}
if ($SelectedThemes.Count -gt 0) {
    $wanted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $SelectedThemes) { [void]$wanted.Add($item) }
    $themeScenarios = @($themeScenarios | Where-Object { $wanted.Contains($_.Name) })
}

function ThemeValue([hashtable]$scenario, [string]$key, $fallback) {
    if ($null -ne $scenario -and $scenario.ContainsKey($key)) { return $scenario[$key] }
    return $fallback
}

function Write-StateJson($state) {
    $json = $state | ConvertTo-Json -Depth 12
    $encoding = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($statePath, $json, $encoding)
}

function Hide-DashboardIfOpen([System.Diagnostics.Process]$process) {
    return
}

function New-TestState([hashtable]$widget, [hashtable]$variant, [hashtable]$scenario = $null) {
    $testWidth = [int]($widget.Width * $variant.ScaleW)
    $testHeight = [int]($widget.Height * $variant.ScaleH)
    if ($variant.Collapsed) { $testHeight = 64 }
    $theme = ThemeValue $scenario "Theme" "Acrylic"
    $preset = ThemeValue $scenario "ThemePreset" "Clear"
    $opacity = [double](ThemeValue $scenario "Opacity" 0.5)
    $textColor = ThemeValue $scenario "Text" "#0D1321"
    $language = ThemeValue $scenario "Language" "zh-TW"
    $corner = [double](ThemeValue $scenario "Corner" 18)
    $nest = [ordered]@{
        Id = [guid]::NewGuid().ToString()
        Kind = $widget.KindValue
        Title = $widget.Title
        Content = "Minimum size regression text"
        FolderPath = $sampleFolder
        Left = $left
        Top = $top
        Width = $testWidth
        Height = $testHeight
        IsVisible = $true
        IsCollapsed = [bool]$variant.Collapsed
        Todos = @(@{ Id=[guid]::NewGuid().ToString(); Text="Long todo text should stay readable"; Done=$false; Color="#FF8A00" })
        Captures = @(@{ Id=[guid]::NewGuid().ToString(); Text="Long quick capture text should wrap inside card"; Source="Manual" })
        Tags = @(@{ Id=[guid]::NewGuid().ToString(); Name="Long tag label"; Color="#FF8A00" })
        Countdowns = @(@{ Id=[guid]::NewGuid().ToString(); Title="Milestone"; Date=(Get-Date).AddDays(30).ToString("o"); Color="#FF8A00" })
        Skin = $theme
        Opacity = $opacity
        FontFamily = "Microsoft JhengHei UI"
        FontSize = 14
        FontColor = $textColor
        City = $cityShenzhen
        Latitude = 22.5431
        Longitude = 114.0579
        WorkStart = "09:00"
        WorkEnd = "18:00"
        WorkDays = @(1,2,3,4,5)
    }

    [ordered]@{
        Nests = @($nest)
        StartWithWindows = $false
        WidgetOpacity = $opacity
        Theme = $theme
        ThemePreset = $preset
        GlobalFontFamily = "Microsoft JhengHei UI"
        GlobalFontSize = 14
        GlobalFontColor = $textColor
        InterfaceFontFamily = "Microsoft JhengHei UI"
        InterfaceFontSize = 13
        ContentFontFamily = "Microsoft JhengHei UI"
        ContentFontSize = 14
        CornerRadius = $corner
        IconSize = 30
        ItemSpacing = 8
        AlignWidgetsToGrid = $false
        WidgetGridSize = 20
        ShowFileExtensions = $true
        EnableCapture = $true
        EnableTodo = $true
        EnableMusic = $true
        EnableWeather = $true
        EnableTags = $true
        EnableSystemMonitor = $true
        ShowReminderSummary = $true
        HeaderPinDefaultMigrated = $true
        OnboardingSeenMachines = @((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography' -ErrorAction SilentlyContinue).MachineGuid)
        ShowFloatingBall = $false
        FloatingBallSnapToEdge = $true
        FloatingBallOpacity = $opacity
        FloatingBallLeft = -1
        FloatingBallTop = -1
        Language = $language
        Hotkeys = @{}
        ToolButtonOrder = @()
        ToolButtonVisibility = @{}
    }
}

function New-OverviewState([hashtable]$scenario) {
    $theme = ThemeValue $scenario "Theme" "Acrylic"
    $preset = ThemeValue $scenario "ThemePreset" "Clear"
    $opacity = [double](ThemeValue $scenario "Opacity" 0.5)
    $textColor = ThemeValue $scenario "Text" "#0D1321"
    $language = ThemeValue $scenario "Language" "zh-TW"
    $corner = [double](ThemeValue $scenario "Corner" 18)
    $cols = [Math]::Max(2, [Math]::Min(3, [int](($screen.Width - 120) / 420)))
    $rows = [Math]::Max(1, [Math]::Min(2, [int](($screen.Height - 100) / 330)))
    $perPage = [Math]::Max(1, $cols * $rows)
    $index = 0
    $cellWidth = 430
    $cellHeight = 330
    $nests = foreach ($widget in $widgets) {
        $col = $index % $cols
        $row = [Math]::Floor(($index % $perPage) / $cols)
        $w = [Math]::Min([Math]::Max([int]($widget.Width * 1.10), 300), 410)
        $h = [Math]::Min([Math]::Max([int]($widget.Height * 1.05), 220), 310)
        $x = $screen.Left + 40 + ($col * $cellWidth)
        $y = $screen.Top + 40 + ($row * $cellHeight)
        $index++
        [ordered]@{
            Id = [guid]::NewGuid().ToString()
            Kind = $widget.KindValue
            Title = $widget.Title
            Content = "Overview regression"
            FolderPath = $sampleFolder
            Left = $x
            Top = $y
            Width = $w
            Height = $h
            IsVisible = $true
            Todos = @(@{ Id=[guid]::NewGuid().ToString(); Text="Check layout"; Done=$false; Color="#FF8A00" })
            Captures = @(@{ Id=[guid]::NewGuid().ToString(); Text="Quick capture sample"; Source="Manual" })
            Tags = @(@{ Id=[guid]::NewGuid().ToString(); Name="Sample"; Color="#FF8A00" })
            Countdowns = @(@{ Id=[guid]::NewGuid().ToString(); Title="Milestone"; Date=(Get-Date).AddDays(30).ToString("o"); Color="#FF8A00" })
            Skin = $theme
            Opacity = $opacity
            FontFamily = "Microsoft JhengHei UI"
            FontSize = 14
            FontColor = $textColor
            City = $cityShenzhen
            Latitude = 22.5431
            Longitude = 114.0579
            WorkStart = "09:00"
            WorkEnd = "18:00"
            WorkDays = @(1,2,3,4,5)
        }
    }
    [ordered]@{
        Nests = @($nests)
        StartWithWindows = $false
        WidgetOpacity = $opacity
        Theme = $theme
        ThemePreset = $preset
        GlobalFontFamily = "Microsoft JhengHei UI"
        GlobalFontSize = 14
        GlobalFontColor = $textColor
        InterfaceFontFamily = "Microsoft JhengHei UI"
        InterfaceFontSize = 13
        ContentFontFamily = "Microsoft JhengHei UI"
        ContentFontSize = 14
        CornerRadius = $corner
        IconSize = 30
        ItemSpacing = 8
        AlignWidgetsToGrid = $false
        WidgetGridSize = 20
        ShowFileExtensions = $true
        EnableCapture = $true
        EnableTodo = $true
        EnableMusic = $true
        EnableWeather = $true
        EnableTags = $true
        EnableSystemMonitor = $true
        ShowReminderSummary = $true
        HeaderPinDefaultMigrated = $true
        OnboardingSeenMachines = @((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Cryptography' -ErrorAction SilentlyContinue).MachineGuid)
        ShowFloatingBall = $false
        FloatingBallSnapToEdge = $true
        FloatingBallOpacity = $opacity
        FloatingBallLeft = -1
        FloatingBallTop = -1
        Language = $language
        Hotkeys = @{}
        ToolButtonOrder = @()
        ToolButtonVisibility = @{}
    }
}

function New-OverviewStatePage([hashtable]$scenario, [int]$page) {
    $cols = [Math]::Max(2, [Math]::Min(3, [int](($screen.Width - 120) / 420)))
    $rows = [Math]::Max(1, [Math]::Min(2, [int](($screen.Height - 100) / 330)))
    $perPage = [Math]::Max(1, $cols * $rows)
    $start = $page * $perPage
    $slice = $widgets | Select-Object -Skip $start -First $perPage
    $base = New-OverviewState $scenario
    $base.Nests = @($base.Nests | Select-Object -Skip $start -First $perPage)
    return $base
}

function Capture-Widget([hashtable]$widget, [hashtable]$variant) {
    $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $bitmap.Size)
    $x = [Math]::Max(0, [int]($left - $screen.Left - 24))
    $y = [Math]::Max(0, [int]($top - $screen.Top - 24))
    $testWidth = [int]($widget.Width * $variant.ScaleW)
    $testHeight = [int]($widget.Height * $variant.ScaleH)
    $width = [Math]::Min([int]($testWidth + 360), $bitmap.Width - $x)
    $height = [Math]::Min([int]($testHeight + 240), $bitmap.Height - $y)
    if ($variant.Collapsed) { $height = [Math]::Min(190, $bitmap.Height - $y) }
    $crop = New-Object System.Drawing.Bitmap $width, $height
    $cropGraphics = [System.Drawing.Graphics]::FromImage($crop)
    $cropGraphics.DrawImage($bitmap, 0, 0, (New-Object System.Drawing.Rectangle $x,$y,$width,$height), [System.Drawing.GraphicsUnit]::Pixel)
    $path = Join-Path $OutputDir "$($widget.Kind)-$($variant.Name).png"
    $crop.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $cropGraphics.Dispose()
    $crop.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}

function Capture-FullScreen([string]$name) {
    $bitmap = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $bitmap.Size)
    $bitmap.Save((Join-Path $OutputDir "overview-$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    $bitmap.Dispose()
}

function Capture-OverviewRegion([string]$name, [int]$count) {
    $cols = [Math]::Max(2, [Math]::Min(3, [int](($screen.Width - 120) / 420)))
    $rows = [Math]::Max(1, [Math]::Min(2, [Math]::Ceiling($count / [double]$cols)))
    $width = [Math]::Min($screen.Width, 70 + ($cols * 430))
    $height = [Math]::Min($screen.Height, 100 + ($rows * 390))
    $full = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
    $fullGraphics = [System.Drawing.Graphics]::FromImage($full)
    $fullGraphics.CopyFromScreen($screen.Left, $screen.Top, 0, 0, $full.Size)
    $fullPath = Join-Path $OutputDir "overview-$name-full-left.png"
    $full.Save($fullPath, [System.Drawing.Imaging.ImageFormat]::Png)

    $crop = New-Object System.Drawing.Bitmap $width, $height
    $cropGraphics = [System.Drawing.Graphics]::FromImage($crop)
    $cropGraphics.DrawImage($full, 0, 0, (New-Object System.Drawing.Rectangle 0,0,$width,$height), [System.Drawing.GraphicsUnit]::Pixel)
    $crop.Save((Join-Path $OutputDir "overview-$name.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    $cropGraphics.Dispose()
    $crop.Dispose()
    $fullGraphics.Dispose()
    $full.Dispose()
}

try {
    foreach ($widget in $widgets) {
        foreach ($variant in $variants) {
            Write-StateJson (New-TestState $widget $variant)
            $process = Start-Process -FilePath (Resolve-Path -LiteralPath $ExePath).Path -ArgumentList "--screenshot-regression" -PassThru
            Start-Sleep -Seconds $StartupDelaySeconds
            Hide-DashboardIfOpen $process
            Capture-Widget $widget $variant
            if (-not $process.HasExited) {
                $null = $process.CloseMainWindow()
                Start-Sleep -Seconds 1
                if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
            }
        }
    }

    foreach ($scenario in $themeScenarios) {
        $cols = [Math]::Max(2, [Math]::Min(3, [int](($screen.Width - 120) / 420)))
        $rows = [Math]::Max(1, [Math]::Min(2, [int](($screen.Height - 100) / 330)))
        $perPage = [Math]::Max(1, $cols * $rows)
        $pages = [Math]::Ceiling($widgets.Count / $perPage)
        for ($page = 0; $page -lt $pages; $page++) {
        Write-StateJson (New-OverviewStatePage $scenario $page)
        $process = Start-Process -FilePath (Resolve-Path -LiteralPath $ExePath).Path -ArgumentList "--screenshot-regression" -PassThru
        Start-Sleep -Seconds $StartupDelaySeconds
        Hide-DashboardIfOpen $process
        $count = [Math]::Min($perPage, $widgets.Count - ($page * $perPage))
        Capture-OverviewRegion "$($scenario.Name)-p$($page + 1)" $count
        if (-not $process.HasExited) {
            $null = $process.CloseMainWindow()
            Start-Sleep -Seconds 1
            if (-not $process.HasExited) { Stop-Process -Id $process.Id -Force }
        }
        }
    }

    $report = @(
        "# BeeX DeskNest minimum widget screenshot regression",
        "",
        "Timestamp: $stamp",
        "Exe: $ExePath",
        "Output: $OutputDir",
        "",
        "Each screenshot uses a clean default BeeX state generated by this script.",
        "The user's normal state.json is backed up before the run and restored afterwards.",
        "Each screenshot is cropped from one widget window only, so the capture does not exceed the tested widget bounds.",
        "",
        "Captured widgets:",
        ($widgets | ForEach-Object { $w=$_; $variants | ForEach-Object { "- $($w.Kind)-$($_.Name): $([int]($w.Width*$_.ScaleW))x$([int]($w.Height*$_.ScaleH))" } }),
        "",
        "Overview theme/language screenshots:",
        ($themeScenarios | ForEach-Object { "- overview-$($_.Name)-p*.png" })
    )
    $report | Set-Content -LiteralPath (Join-Path $OutputDir "README.md") -Encoding UTF8
}
finally {
    if ($hadState) {
        Copy-Item -LiteralPath $backupPath -Destination $statePath -Force
    } elseif (Test-Path -LiteralPath $statePath) {
        Remove-Item -LiteralPath $statePath -Force
    }
}

Write-Host "Regression screenshots saved to: $OutputDir"
