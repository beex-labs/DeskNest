param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$releaseRoot = Join-Path $projectRoot "releases\$timestamp"
$portableDir = Join-Path $releaseRoot "portable-net8"
$modelsSrc = Join-Path $projectRoot "models-src"

$requiredModels = @("PP-OCRv5_mobile_det", "PP-OCRv5_mobile_rec", "PP-FormulaNet_plus-S", "SLANet_plus")
foreach ($model in $requiredModels) {
    if (-not (Test-Path (Join-Path $modelsSrc "$model\inference.pdiparams"))) {
        throw "Model missing: $modelsSrc\$model. Run scripts\download-models.ps1 first."
    }
}

New-Item -ItemType Directory -Path $portableDir -Force | Out-Null

# 鍙屼晶杞︼細BeeX_OCR.exe = MKL 杩愯鏃讹紙鏂囧瓧 OCR锛宱neDNN 鍔犻€燂級锛?
#         BeeX_Formula.exe = openblas 杩愯鏃讹紙鍏紡妯″瀷鍦?oneDNN 鍐呮牳涓嬪繀宕╋級
$variants = @(
    @{ Assembly = "BeeX_OCR"; RuntimeProp = @() },
    @{ Assembly = "BeeX_Formula"; RuntimeProp = @("/p:PaddleRuntime=openblas") }
)

foreach ($variant in $variants) {
    $publishDir = Join-Path $projectRoot ("obj\release-publish\$timestamp-" + $variant.Assembly)
    New-Item -ItemType Directory -Path $publishDir -Force | Out-Null

    $publishArgs = @(
        "publish",
        (Join-Path $projectRoot "BeeX_OCR.csproj"),
        "-f", "net8.0-windows10.0.19041.0",
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $publishDir,
        "--self-contained", "true",
        "/p:AssemblyName=$($variant.Assembly)",
        "/p:SelfContained=true",
        "/p:PublishSelfContained=true",
        "/p:PublishSingleFile=true",
        "/p:IncludeNativeLibrariesForSelfExtract=true",
        "/p:EnableCompressionInSingleFile=true",
        "/p:DebugType=None",
        "/p:DebugSymbols=false",
        "/p:PublishDocumentationFile=false",
        "/p:Optimize=true",
        "/p:PublishReadyToRun=false",
        "/p:PublishTrimmed=false"
    ) + $variant.RuntimeProp

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) {
        throw ($variant.Assembly + " publish failed with exit code $LASTEXITCODE")
    }

    Get-ChildItem -Path $publishDir -Recurse -File -Include *.pdb,*.xml,*.md,*.config | ForEach-Object {
        Remove-Item -LiteralPath $_.FullName -Force
    }

    $publishedExe = Join-Path $publishDir ($variant.Assembly + ".exe")
    if (-not (Test-Path -LiteralPath $publishedExe)) {
        throw "Portable executable was not found: $publishedExe"
    }

    Copy-Item -LiteralPath $publishedExe -Destination (Join-Path $portableDir ($variant.Assembly + ".exe")) -Force
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

# 渚ц溅甯冨眬锛氫袱涓?exe + 鍏辩敤 models\<妯″瀷鐩綍>锛堟ā鍨嬩笉鍐呭祵杩涘崟鏂囦欢锛屼究浜庡閲忔洿鏂帮級
# zip 鐢ㄥ浐瀹氬悕 beex-ocr-win-x64.zip锛欴eskNest 閫氳繃 GitHub Release 鐨?
# releases/latest/download/beex-ocr-win-x64.zip 鍦ㄧ嚎瀹夎锛屽彂鐗堢洿鎺ヤ笂浼犳鏂囦欢鍗冲彲
$portableModels = Join-Path $portableDir "models"
$portableZip = Join-Path $releaseRoot "beex-ocr-win-x64.zip"

New-Item -ItemType Directory -Path $portableModels -Force | Out-Null
foreach ($model in $requiredModels) {
    Copy-Item -Path (Join-Path $modelsSrc $model) -Destination (Join-Path $portableModels $model) -Recurse -Force
}

Compress-Archive -Path "$portableDir\*" -DestinationPath $portableZip -Force

$dirSize = (Get-ChildItem $portableDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
$zipSize = (Get-Item $portableZip).Length / 1MB
Write-Host ("Release generated: {0}" -f $releaseRoot)
Get-ChildItem $portableDir -File | ForEach-Object { Write-Host ("  {0}  {1:N1} MB" -f $_.Name, ($_.Length/1MB)) }
Write-Host ("Portable dir total: {0:N1} MB, zip: {1:N1} MB" -f $dirSize, $zipSize)

