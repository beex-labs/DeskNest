# Verifies the released dual sidecars end-to-end via the --serve protocol.
# Paths resolve relative to this script; override the release dir with -Rel <path-to-portable-net8>.
param(
    [string]$Rel = "",
    [string]$Tests = $PSScriptRoot
)
if (-not $Rel) {
    $relRoot = Join-Path (Split-Path $PSScriptRoot -Parent) "releases"
    if (Test-Path $relRoot) {
        $Rel = Get-ChildItem $relRoot -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "portable-net8" } |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    }
}
if (-not $Rel -or -not (Test-Path $Rel)) {
    Write-Error "Release portable dir not found. Pass -Rel <path-to-portable-net8>."
    exit 1
}
$rel = $Rel
$t = $Tests

function Test-Sidecar($exe, $role, $cmd, $img, $out) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $exe
    $psi.Arguments = "--serve --serve-role $role"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.CreateNoWindow = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ready = $p.StandardOutput.ReadLine(); $readyMs = $sw.ElapsedMilliseconds
    $sw.Restart()
    $p.StandardInput.WriteLine("$cmd`t$img`t$out")
    $r1 = $p.StandardOutput.ReadLine(); $ms1 = $sw.ElapsedMilliseconds
    $sw.Restart()
    $p.StandardInput.WriteLine("$cmd`t$img`t$out")
    $r2 = $p.StandardOutput.ReadLine(); $ms2 = $sw.ElapsedMilliseconds
    $p.StandardInput.WriteLine("EXIT")
    $p.WaitForExit(5000) | Out-Null
    if (!$p.HasExited) { $p.Kill() }
    "$role : READY=$ready (+${readyMs}ms)  1st=$r1 (+${ms1}ms)  2nd=$r2 (+${ms2}ms)"
}

Test-Sidecar "$rel\BeeX_OCR.exe" "ocr" "OCR" "$t\text_sample.png" "$t\rel_ocr.txt"
Test-Sidecar "$rel\BeeX_Formula.exe" "formula" "FORMULA" "$t\formula_sample.png" "$t\rel_formula.txt"
"--- formula latex head ---"
[System.IO.File]::ReadAllText("$t\rel_formula.txt").Substring(0, 70)
"--- ocr text ---"
[System.IO.File]::ReadAllLines("$t\rel_ocr.txt") | Select-Object -First 2 | ForEach-Object { $_ }
