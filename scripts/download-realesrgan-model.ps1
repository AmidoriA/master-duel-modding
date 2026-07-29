# Download Real-ESRGAN anime video v3 ONNX for Floowan OF art upscale.
# Weights are small (~2.4 MB) and must not be committed.
#
# Usage:
#   pwsh -File scripts/download-realesrgan-model.ps1
#   pwsh -File scripts/download-realesrgan-model.ps1 -ModelDirectory "D:\models\floowan-realesrgan"

param(
    [string]$ModelDirectory = $(Join-Path $env:LOCALAPPDATA "Floowan\models\realesrgan")
)

$ErrorActionPreference = "Stop"

$ModelName = "RealESR-AnimeVideo-v3_x4.onnx"
$Url = "https://huggingface.co/tidus2102/Real-ESRGAN/resolve/main/$ModelName"
$ExpectedSha256 = "00ece3ac21c43ee31459216b5174b2cea0c5325044c5142aeb840f4890e175ff"

New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null
$ModelPath = Join-Path $ModelDirectory $ModelName

if (Test-Path $ModelPath) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $ModelPath).Hash.ToLowerInvariant()
    if ($hash -eq $ExpectedSha256) {
        Write-Host "Real-ESRGAN ONNX already present in $ModelDirectory"
        exit 0
    }
    Write-Host "Checksum mismatch; re-downloading…"
    Remove-Item -Force $ModelPath
}

Write-Host "Downloading $Url ..."
Invoke-WebRequest -Uri $Url -OutFile $ModelPath -UseBasicParsing

$hash = (Get-FileHash -Algorithm SHA256 -Path $ModelPath).Hash.ToLowerInvariant()
if ($hash -ne $ExpectedSha256) {
    Remove-Item -Force $ModelPath
    throw "SHA-256 mismatch for $ModelName (got $hash, expected $ExpectedSha256)"
}

Write-Host "Ready:"
Write-Host "  $ModelPath"
