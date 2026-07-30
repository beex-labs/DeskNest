<#
.SYNOPSIS
    Enumerate all Windows System Media Transport Controls (SMTC) sessions and dump raw properties.

.DESCRIPTION
    The BeeX DeskNest music widget relies on GlobalSystemMediaTransportControls (SMTC)
    to read the currently playing track and its progress. If a player (e.g. NetEase
    Cloud Music) does not register an SMTC session, or registers but does not report
    TimelineProperties (position / duration), the widget shows "no media" or cannot
    get progress.

    This script calls the exact same WinRT API the widget uses and prints every
    session's raw data, so we can tell whether the problem is on the player side
    or the widget side.

.USAGE
    1. Make sure NetEase Cloud Music (or another player) is actively Playing.
    2. Run in PowerShell:
         powershell -ExecutionPolicy Bypass -File .\Dump-SmtcSessions.ps1
    3. Send the output back.
#>

$ErrorActionPreference = 'Stop'

Write-Host "==== SMTC Session Diagnostics ====" -ForegroundColor Cyan
Write-Host ("Time: {0}" -f (Get-Date))
Write-Host ("OS  : {0}" -f [System.Environment]::OSVersion.VersionString)
Write-Host ""

# --- Load WinRT projections and prepare an await helper ----------------------
Add-Type -AssemblyName System.Runtime.WindowsRuntime | Out-Null

$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() |
    Where-Object {
        $_.Name -eq 'AsTask' -and
        $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($WinRtTask, $ResultType) {
    $asTask  = $asTaskGeneric.MakeGenericMethod($ResultType)
    $netTask = $asTask.Invoke($null, @($WinRtTask))
    $netTask.Wait(-1) | Out-Null
    $netTask.Result
}

# Trigger WinRT type projection
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSession, Windows.Media.Control, ContentType = WindowsRuntime]        | Out-Null
[Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime] | Out-Null

# --- Get the SessionManager --------------------------------------------------
$mgr = Await ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager]::RequestAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager])

$sessions = @($mgr.GetSessions())
$current  = $mgr.GetCurrentSession()

Write-Host ("Total sessions detected: {0}" -f $sessions.Count) -ForegroundColor Yellow
if ($current) {
    Write-Host ("Current session (GetCurrentSession): {0}" -f $current.SourceAppUserModelId) -ForegroundColor Yellow
} else {
    Write-Host "Current session (GetCurrentSession): <null>" -ForegroundColor Yellow
}
Write-Host ""

if ($sessions.Count -eq 0) {
    Write-Host "!! No application registered any SMTC session." -ForegroundColor Red
    Write-Host "   If NetEase is playing right now, this build of NetEase does NOT integrate with SMTC." -ForegroundColor Red
    return
}

$i = 0
foreach ($s in $sessions) {
    $i++
    Write-Host ("---- Session #{0} -------------------------------------------" -f $i) -ForegroundColor Green
    Write-Host ("SourceAppUserModelId : {0}" -f $s.SourceAppUserModelId)

    # Playback info
    try {
        $pi = $s.GetPlaybackInfo()
        Write-Host ("PlaybackStatus       : {0}" -f $pi.PlaybackStatus)
        Write-Host ("PlaybackType         : {0}" -f $pi.PlaybackType)
        Write-Host ("PlaybackRate         : {0}" -f $pi.PlaybackRate)
        $ctrls = $pi.Controls
        Write-Host ("Controls.IsPlaybackPositionEnabled : {0}" -f $ctrls.IsPlaybackPositionEnabled)
        Write-Host ("Controls Play/Pause/Next/Prev : {0} / {1} / {2} / {3}" -f `
            $ctrls.IsPlayEnabled, $ctrls.IsPauseEnabled, $ctrls.IsNextEnabled, $ctrls.IsPreviousEnabled)
    } catch {
        Write-Host ("GetPlaybackInfo failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }

    # Timeline (progress) -- the key to "cannot get progress"
    try {
        $tl = $s.GetTimelineProperties()
        Write-Host ("Timeline.StartTime       : {0}" -f $tl.StartTime)
        Write-Host ("Timeline.EndTime         : {0}" -f $tl.EndTime)
        Write-Host ("Timeline.Position        : {0}" -f $tl.Position)
        Write-Host ("Timeline.MinSeekTime     : {0}" -f $tl.MinSeekTime)
        Write-Host ("Timeline.MaxSeekTime     : {0}" -f $tl.MaxSeekTime)
        Write-Host ("Timeline.LastUpdatedTime : {0}" -f $tl.LastUpdatedTime)
        $durSecs = ($tl.EndTime - $tl.StartTime).TotalSeconds
        if ($durSecs -le 0) {
            Write-Host "   >> Timeline is empty (duration <= 0): this session does NOT report progress." -ForegroundColor Red
        } else {
            $posSecs = $tl.Position.TotalSeconds
            Write-Host ("   >> Duration ~{0:n0}s, position ~{1:n0}s." -f $durSecs, $posSecs) -ForegroundColor Cyan
        }
    } catch {
        Write-Host ("GetTimelineProperties failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }

    # Media properties (title / artist)
    try {
        $props = Await ($s.TryGetMediaPropertiesAsync()) ([Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties])
        Write-Host ("Title  : {0}" -f $props.Title)
        Write-Host ("Artist : {0}" -f $props.Artist)
        Write-Host ("Album  : {0}" -f $props.AlbumTitle)
        Write-Host ("Thumbnail present : {0}" -f ($props.Thumbnail -ne $null))
    } catch {
        Write-Host ("TryGetMediaPropertiesAsync failed: {0}" -f $_.Exception.Message) -ForegroundColor Red
    }

    Write-Host ""
}

Write-Host "==== End of diagnostics ====" -ForegroundColor Cyan
