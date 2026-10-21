<#
.SYNOPSIS
  Downloads the public demo video: "Tears of Steel" (1080p), a live-action short by the Blender Foundation.

.DESCRIPTION
  Licence: Creative Commons Attribution 3.0 — credit "(CC) Blender Foundation | mango.blender.org" whenever it is shown.
  Used instead of private footage for demos, screenshots and benchmarks: anyone can reproduce the numbers, and no real
  person's privacy is involved. The mirror is listed on the official download page (https://mango.blender.org/download/).

  The Blender Foundation publishes no checksum, so the file is pinned by size and by the SHA-256 recorded on first
  download (2026-09-24). Saved to media/ (git-ignored).
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$media = Join-Path $root 'media'
$file = Join-Path $media 'tears_of_steel_1080p.mov'
$archive = Join-Path $media 'tears_of_steel_1080p.download' # the mirror serves a ZIP (stored, not compressed) named .mov
$url = 'https://ftp.halifax.rwth-aachen.de/blender/demo/movies/ToS/tears_of_steel_1080p.mov'
$expectedArchiveSize = 583774281
$expectedSize = 583774083
$sha256 = '99486359BE7E3681168A0FE94E1CBB0284C48B57B2A3CE7DF4FA75E185987A15'

New-Item -ItemType Directory -Force $media | Out-Null
if ((Test-Path $file) -and (Get-Item $file).Length -eq $expectedSize) {
    if (-not $sha256 -or (Get-FileHash $file -Algorithm SHA256).Hash -eq $sha256) {
        Write-Host "OK (cached)  $file"
        return
    }
}

Write-Host "Downloading  $url  (557 MB)"
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest's progress bar slows large downloads considerably
Invoke-WebRequest $url -OutFile $archive
if ((Get-Item $archive).Length -ne $expectedArchiveSize) {
    Remove-Item $archive -Force
    throw "Unexpected download size $((Get-Item $archive).Length) (expected $expectedArchiveSize)."
}

# Despite its name, the mirror's file is a ZIP archive holding the real .mov: extract it.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $entry = $zip.Entries | Where-Object { $_.Name -eq 'tears_of_steel_1080p.mov' } | Select-Object -First 1
    if (-not $entry) { throw 'The archive does not contain tears_of_steel_1080p.mov.' }
    [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $file, $true)
}
finally {
    $zip.Dispose()
}

Remove-Item $archive -Force
$hash = (Get-FileHash $file -Algorithm SHA256).Hash
if ((Get-Item $file).Length -ne $expectedSize -or ($sha256 -and $hash -ne $sha256)) {
    Remove-Item $file -Force
    throw "Extracted video does not match (size $((Get-Item $file).Length), SHA-256 $hash)."
}

Write-Host "OK           $file  SHA-256 $hash"
Write-Host 'Credit when shown: (CC) Blender Foundation | mango.blender.org'