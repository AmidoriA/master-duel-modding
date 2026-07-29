# Download SAM 2 Tiny ONNX models for Floowan point cutout.
# Weights are large (~148 MB zip) and must not be committed.
#
# Usage:
#   pwsh -File scripts/download-sam2-models.ps1
#   pwsh -File scripts/download-sam2-models.ps1 -ModelDirectory "D:\models\floowan-sam2"

param(
    [string]$ModelDirectory = $(Join-Path $env:LOCALAPPDATA "Floowan\models\sam2")
)

$ErrorActionPreference = "Stop"

$ZipName = "sam2_hiera_tiny.zip"
$Url = "https://huggingface.co/vietanhdev/segment-anything-2-onnx-models/resolve/main/$ZipName"
$ExpectedSha256 = "7454c3afd835b2acaad863afe3acb11f4e4af039c96e886989ad3873a338e1ec"
$Encoder = "sam2_hiera_tiny.encoder.onnx"
$Decoder = "sam2_hiera_tiny.decoder.onnx"

New-Item -ItemType Directory -Force -Path $ModelDirectory | Out-Null
$ZipPath = Join-Path $ModelDirectory $ZipName
$EncoderPath = Join-Path $ModelDirectory $Encoder
$DecoderPath = Join-Path $ModelDirectory $Decoder

if ((Test-Path $EncoderPath) -and (Test-Path $DecoderPath)) {
    Write-Host "SAM 2 Tiny ONNX already present in $ModelDirectory"
    exit 0
}

if (-not (Test-Path $ZipPath)) {
    Write-Host "Downloading $Url ..."
    Invoke-WebRequest -Uri $Url -OutFile $ZipPath -UseBasicParsing
}

$hash = (Get-FileHash -Algorithm SHA256 -Path $ZipPath).Hash.ToLowerInvariant()
if ($hash -ne $ExpectedSha256) {
    Remove-Item -Force $ZipPath
    throw "SHA-256 mismatch for $ZipName (got $hash, expected $ExpectedSha256)"
}

Write-Host "Extracting to $ModelDirectory ..."
Expand-Archive -Path $ZipPath -DestinationPath $ModelDirectory -Force

if (-not (Test-Path $EncoderPath) -or -not (Test-Path $DecoderPath)) {
    throw "Extracted zip did not contain $Encoder / $Decoder"
}

Write-Host "Ready:"
Write-Host "  $EncoderPath"
Write-Host "  $DecoderPath"
