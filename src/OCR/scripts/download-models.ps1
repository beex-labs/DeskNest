# Downloads the official PaddleOCR inference models required by BeeX_OCR
# into BeeX_OCR\models-src\. Run once before building a release.
param(
    [string]$BaseUrl = "https://paddle-model-ecology.bj.bcebos.com/paddlex/official_inference_model/paddle3.0.0"
)

$ErrorActionPreference = "Stop"

$projectRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$modelsSrc = Join-Path $projectRoot "models-src"
New-Item -ItemType Directory -Path $modelsSrc -Force | Out-Null

$models = @(
    "PP-OCRv5_mobile_det",
    "PP-OCRv5_mobile_rec",
    "PP-FormulaNet_plus-S"
)
# 娉細琛ㄦ牸缁撴瀯妯″瀷 SLANet_ch 鏉ヨ嚜鏃у簱鍦板潃锛堝惈缁撴瀯瀛楀吀锛夛紝鍗曠嫭涓嬭浇锛?
# https://paddleocr.bj.bcebos.com/ppstructure/models/slanet/ch_ppstructure_mobile_v2.0_SLANet_infer.tar
# https://raw.githubusercontent.com/PaddlePaddle/PaddleOCR/main/ppocr/utils/dict/table_structure_dict_ch.txt

foreach ($model in $models) {
    $targetDir = Join-Path $modelsSrc $model
    if ((Test-Path (Join-Path $targetDir "inference.pdiparams")) -or (Test-Path (Join-Path $targetDir "inference.json"))) {
        Write-Host "[skip] $model already present."
        continue
    }

    $tarName = "$model" + "_infer.tar"
    $tarPath = Join-Path $modelsSrc $tarName
    $url = "$BaseUrl/$tarName"

    Write-Host "[down] $url"
    Invoke-WebRequest -Uri $url -OutFile $tarPath -UseBasicParsing

    Write-Host "[extr] $tarName"
    tar -xf $tarPath -C $modelsSrc
    Remove-Item -LiteralPath $tarPath -Force

    # The tar extracts to <model>_infer or <model>; normalize to <model>.
    $extractedInfer = Join-Path $modelsSrc ($model + "_infer")
    if (Test-Path $extractedInfer) {
        if (Test-Path $targetDir) { Remove-Item -LiteralPath $targetDir -Recurse -Force }
        Rename-Item -LiteralPath $extractedInfer -NewName $model
    }

    if (-not (Test-Path $targetDir)) {
        throw "Model extraction failed for $model"
    }
}

Write-Host "All models ready under $modelsSrc"
Get-ChildItem $modelsSrc -Directory | ForEach-Object {
    $size = (Get-ChildItem $_.FullName -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB
    "{0}  {1:N1} MB" -f $_.Name, $size
}

