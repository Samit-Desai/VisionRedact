<#
.SYNOPSIS
  Downloads Cisco's OpenH264 1.8.0 (win64) so exports to MP4 use H.264 ("avc1") instead of MPEG-4 Part 2 ("mp4v").

.DESCRIPTION
  OpenCV's FFmpeg plugin (opencv_videoio_ffmpeg4130_64.dll) loads "openh264-1.8.0-win64.dll" at run time for H.264
  encoding; without it VideoWriter cannot open "avc1" and the app falls back to "mp4v".

  Licence: Cisco's binary licence (http://www.openh264.org/BINARY_LICENSE.txt). Cisco pays the MPEG LA H.264 royalties
  only for binaries downloaded from Cisco, so the DLL is downloaded by each user (this script) and never committed or
  redistributed with the repository.

  Verification: Cisco publishes no SHA for the Windows archive; its Windows DLLs are Authenticode-signed instead. The
  script checks the archive size and that the extracted DLL has a valid signature from Cisco, and deletes it otherwise.

  Needs Python (bz2 module) or 7-Zip to decompress the .bz2 archive.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$native = Join-Path $root 'native'
$dll = Join-Path $native 'openh264-1.8.0-win64.dll'
$archive = "$dll.bz2"
$url = 'https://github.com/cisco/openh264/releases/download/v1.8.0/openh264-1.8.0-win64.dll.bz2'
$expectedArchiveSize = 387876

function Assert-CiscoSignature($path) {
    $signature = Get-AuthenticodeSignature $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Cisco') {
        Remove-Item $path -Force
        throw "Rejected $path : signature status '$($signature.Status)', signer '$($signature.SignerCertificate.Subject)'."
    }

    Write-Host "Signature OK ($($signature.SignerCertificate.Subject))"
}

New-Item -ItemType Directory -Force $native | Out-Null
if (Test-Path $dll) {
    Assert-CiscoSignature $dll
    Write-Host "OK (cached)  $dll"
    return
}

Write-Host "Downloading  $url"
Invoke-WebRequest $url -OutFile $archive
$size = (Get-Item $archive).Length
if ($size -ne $expectedArchiveSize) {
    Remove-Item $archive -Force
    throw "Unexpected archive size $size (expected $expectedArchiveSize)."
}

$python = Get-Command python -ErrorAction SilentlyContinue
$sevenZip = Get-Command 7z -ErrorAction SilentlyContinue
if ($python) {
    & $python.Source -c "import bz2,shutil,sys; shutil.copyfileobj(bz2.open(sys.argv[1]), open(sys.argv[2],'wb'))" $archive $dll
} elseif ($sevenZip) {
    & $sevenZip.Source e $archive "-o$native" -y | Out-Null
} else {
    throw 'Neither Python nor 7-Zip was found to decompress the .bz2 archive.'
}

Remove-Item $archive -Force
Assert-CiscoSignature $dll
Write-Host "OK           $dll  (rebuild: the WPF project copies native\*.dll next to the app)"
