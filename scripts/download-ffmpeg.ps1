<#
.SYNOPSIS
  Downloads FFmpeg 9.0 (win64, LGPL, shared) for faster MP4 export with audio.

.DESCRIPTION
  Build: BtbN/FFmpeg-Builds (linked from ffmpeg.org's download page), variant "lgpl-shared": ffmpeg.exe + DLLs, LGPL
  licence, includes the hardware H.264 encoders (NVIDIA NVENC, Intel QSV, AMD AMF, Media Foundation) but not the GPL
  x264 encoder. The app streams raw BGR frames to ffmpeg.exe and copies the source audio track into the export.

  The "latest" builds are rebuilt daily, so the exact archive is pinned by SHA-256 (taken from the release's
  checksums.sha256 on 2026-09-24). If the archive has been rebuilt since, the hash check fails: download the
  current checksums.sha256 from the release page, verify, and update $sha256 below.

  Installed into ffmpeg/ (git-ignored). Without it the app exports through OpenCV (no audio, CPU encoding).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$target = Join-Path $root 'ffmpeg'
$name = 'ffmpeg-n9.0-latest-win64-lgpl-shared-9.0'
$url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/$name.zip"
$sha256 = '16E3E99DC13CD31AE1EE2130F4D8E5213D1D08891833C3DA03CB5F8D7EDC4682'

$exe = Join-Path $target 'bin\ffmpeg.exe'
if (Test-Path $exe) {
    Write-Host "OK (installed)  $exe"
    return
}

$archive = Join-Path ([IO.Path]::GetTempPath()) "$name.zip"
Write-Host "Downloading  $url"
Invoke-WebRequest $url -OutFile $archive
$actual = (Get-FileHash $archive -Algorithm SHA256).Hash
if ($actual -ne $sha256) {
    Remove-Item $archive -Force
    throw "Hash mismatch for $name.zip (got $actual). The 'latest' build may have been rebuilt; see the script header."
}

Write-Host "Hash OK      $actual"
$staging = Join-Path ([IO.Path]::GetTempPath()) "$name-extract"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Expand-Archive $archive -DestinationPath $staging
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item (Join-Path $staging "$name\*") $target -Recurse -Force
Remove-Item $staging -Recurse -Force
Remove-Item $archive -Force
& $exe -hide_banner -version | Select-Object -First 1
Write-Host "OK           $exe  (rebuild: the WPF project copies ffmpeg\bin next to the app)"
