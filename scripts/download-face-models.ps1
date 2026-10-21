<#
.SYNOPSIS
  Downloads the two small models used to group faces into people (People tab → "Identify people").

.DESCRIPTION
  - face_detection_yunet_2023mar.onnx (~233 KB, OpenCV Zoo, MIT): finds 5 face landmarks (eyes, nose, mouth corners)
    so each face can be aligned before recognition.
  - w600k_mbf.onnx (~13.6 MB, InsightFace ArcFace MobileFaceNet from the buffalo_s pack, via a Hugging Face mirror):
    turns an aligned 112x112 face into a 512-number fingerprint. InsightFace's pretrained weights are licensed for
    non-commercial research only - fine for this demo / learning project; a commercial product would need a licence
    or a permissively licensed recognizer (e.g. OpenCV Zoo SFace).

  Both files are pinned by SHA-256 (taken on 2026-10-10) and saved into models/ (git-ignored).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'models'
New-Item -ItemType Directory -Force $target | Out-Null

$models = @(
    @{ Name = 'face_detection_yunet_2023mar.onnx'
       Url = 'https://huggingface.co/opencv/face_detection_yunet/resolve/main/face_detection_yunet_2023mar.onnx'
       Sha256 = '8F2383E4DD3CFBB4553EA8718107FC0423210DC964F9F4280604804ED2552FA4' },
    @{ Name = 'w600k_mbf.onnx'
       Url = 'https://huggingface.co/ykk648/face_lib/resolve/main/face_embedding/w600k_mbf.onnx'
       Sha256 = '9CC6E4A75F0E2BF0B1AED94578F144D15175F357BDC05E815E5C4A02B319EB4F' }
)

foreach ($model in $models) {
    $path = Join-Path $target $model.Name
    if ((Test-Path $path) -and (Get-FileHash $path -Algorithm SHA256).Hash -eq $model.Sha256) {
        Write-Host "OK (installed)  $path"
        continue
    }

    Write-Host "Downloading  $($model.Url)"
    $temp = "$path.download"
    Invoke-WebRequest $model.Url -OutFile $temp
    $actual = (Get-FileHash $temp -Algorithm SHA256).Hash
    if ($actual -ne $model.Sha256) {
        Remove-Item $temp -Force
        throw "Hash mismatch for $($model.Name) (got $actual)."
    }

    Move-Item $temp $path -Force
    Write-Host "OK           $path"
}

Write-Host 'Rebuild: the WPF project copies models\*.onnx next to the app.'
