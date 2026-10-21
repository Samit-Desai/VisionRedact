<#
.SYNOPSIS
  Downloads the three built-in face detection models (and UltraFace's reference test data).

.DESCRIPTION
  - UltraFace version-RFB-320 (ONNX Model Zoo, MIT licence, ~1.3 MB): fastest.
  - YOLO11n face (Hugging Face AdamCodd/YOLOv11n-face-detection; the card says Apache-2.0, but Ultralytics-trained models are AGPL-3.0 by Ultralytics' terms; ~10.6 MB): balanced.
  - YOLO26l face (Hugging Face uralman/yolo26l-widerface, AGPL-3.0, ~99.6 MB): most accurate, NMS-free.
    Skip it with -SkipLargeModels.
  Test data: the model zoo's reference input/outputs, used by the real-inference integration tests.
  Files are verified by SHA-256 so a truncated or tampered download is rejected.
  Both are git-ignored (binary artefacts are not committed).
#>
param([switch]$SkipTestData, [switch]$SkipLargeModels)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$models = Join-Path $root 'models'
New-Item -ItemType Directory -Force $models | Out-Null

function Get-Verified($url, $path, $sha256) {
    if ((Test-Path $path) -and ((Get-FileHash $path -Algorithm SHA256).Hash -eq $sha256)) {
        Write-Host "OK (cached)  $path"; return
    }
    Write-Host "Downloading  $url"
    Invoke-WebRequest $url -OutFile $path
    $actual = (Get-FileHash $path -Algorithm SHA256).Hash
    if ($actual -ne $sha256) { Remove-Item $path; throw "Hash mismatch for $path (got $actual)" }
    Write-Host "OK           $path"
}

Get-Verified 'https://huggingface.co/onnxmodelzoo/version-RFB-320/resolve/main/version-RFB-320.onnx' `
    (Join-Path $models 'version-RFB-320.onnx') '34CD7E60AEFF28744C657DE7A3DC64E872D506741DE66987F3426F2B79F88017'

Get-Verified 'https://huggingface.co/AdamCodd/YOLOv11n-face-detection/resolve/main/model.onnx' `
    (Join-Path $models 'yolo11n-face.onnx') '2DFE14171F5B76A05F9BCF0DAC7F94B7BFF4416B1F29EFF7C9EF5830F51C5719'

if (-not $SkipLargeModels) {
    Get-Verified 'https://huggingface.co/uralman/yolo26l-widerface/resolve/main/model.onnx' `
        (Join-Path $models 'yolo26l-face.onnx') '8672C0AEA1A14CC8F318BA0E999D14DCAA430543AB3CAF9C369662BB0491636E'
}

if (-not $SkipTestData) {
    $testdata = Join-Path $models 'testdata'
    New-Item -ItemType Directory -Force $testdata | Out-Null
    $archive = Join-Path $testdata 'version-RFB-320.tar.gz'
    Get-Verified 'https://media.githubusercontent.com/media/onnx/models/main/validated/vision/body_analysis/ultraface/models/version-RFB-320.tar.gz' `
        $archive '628D0DD3E0288ADB821F211E13D4E97F6D6F4527237339606732DFFA6F19D381'
    tar -xzf $archive -C $testdata 'version-RFB-320/test_data_set_0'
    Write-Host "Extracted    $testdata\version-RFB-320\test_data_set_0"
}
