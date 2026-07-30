#Requires -Version 5.1
<#
.SYNOPSIS
  Publish Masterduel modding tool (single-file win-x64) and zip the release artifact.

.DESCRIPTION
  Mirrors the proven v0.0.1 workflow: PublishSingleFile + sidecars, then
  MasterDuelModding-vX.Y.Z-win-x64.zip. Does not tag or upload unless
  -CreateGitHubRelease is passed.

.PARAMETER Version
  Version without leading v (e.g. 0.0.2). Tag/release use v$Version.

.PARAMETER SkipPublish
  Re-stage/zip from existing artifacts/publish/win-x64-sf.

.PARAMETER CreateGitHubRelease
  Create annotated tag vX.Y.Z (if missing), push tag, and gh release create
  or upload --clobber. Never force-pushes tags.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+')]
    [string] $Version,

    [switch] $SkipPublish,

    [switch] $CreateGitHubRelease
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $repoRoot

$tag = "v$Version"
$publishDir = "artifacts/publish/win-x64-sf"
$stageDir = "artifacts/publish/win-x64-sf-stage"
$zipName = "MasterDuelModding-$tag-win-x64.zip"
$zipPath = Join-Path "artifacts" $zipName
$exeName = "Floowan.Desktop.exe"
$proj = "src/Floowan.Desktop/Floowan.Desktop.csproj"

function Assert-Required([string] $Path) {
    if (-not (Test-Path $Path)) {
        throw "Missing required path: $Path"
    }
}

Write-Host "=== Version check (informational) ==="
$csproj = Get-Content $proj -Raw
if ($csproj -notmatch "<Version>$([regex]::Escape($Version))</Version>") {
    Write-Warning "csproj <Version> may not match $Version - confirm before tagging."
}

if (-not $SkipPublish) {
    Write-Host "=== dotnet publish (single-file win-x64) ==="
    if (Test-Path $publishDir) {
        Remove-Item $publishDir -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

    & dotnet publish $proj `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $publishDir
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}
else {
    Assert-Required $publishDir
}

Write-Host "=== Stage zip contents ==="
Assert-Required (Join-Path $publishDir $exeName)
Assert-Required (Join-Path $publishDir "database.db")
Assert-Required (Join-Path $publishDir "classdata.tpk")
Assert-Required (Join-Path $publishDir "THIRD_PARTY_NOTICES.txt")
Assert-Required (Join-Path $publishDir "frames")
Assert-Required (Join-Path $publishDir "locales")

$localeYamls = @(Get-ChildItem (Join-Path $publishDir "locales") -Filter "*.yaml" -File -ErrorAction SilentlyContinue)
if ($localeYamls.Count -lt 1) {
    throw "Missing locale YAML files under $publishDir/locales (expected en-US.yaml, th-TH.yaml, ...)."
}
if (-not (Test-Path (Join-Path $publishDir "locales/en-US.yaml"))) {
    throw "Missing required baseline locale: $publishDir/locales/en-US.yaml"
}

if (Test-Path $stageDir) {
    Remove-Item $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
Copy-Item (Join-Path $publishDir $exeName) $stageDir
Copy-Item (Join-Path $publishDir "database.db") $stageDir
Copy-Item (Join-Path $publishDir "THIRD_PARTY_NOTICES.txt") $stageDir
Copy-Item (Join-Path $publishDir "classdata.tpk") $stageDir
Copy-Item (Join-Path $publishDir "frames") (Join-Path $stageDir "frames") -Recurse
Copy-Item (Join-Path $publishDir "locales") (Join-Path $stageDir "locales") -Recurse

$dlls = @(Get-ChildItem $stageDir -Filter "*.dll" -File -ErrorAction SilentlyContinue)
if ($dlls.Count -gt 0) {
    Write-Warning ("Staged loose DLLs (native fallback?): " + ($dlls.Name -join ", "))
}

New-Item -ItemType Directory -Force -Path "artifacts" | Out-Null
if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}
Compress-Archive -Path (Join-Path $stageDir "*") -DestinationPath $zipPath

$zipItem = Get-Item $zipPath
$sizeMb = [math]::Round($zipItem.Length / 1MB, 1)
Write-Host "=== Zip ready: $zipPath ($sizeMb MB) ==="
Get-ChildItem $stageDir | ForEach-Object {
    if ($_.PSIsContainer) {
        $n = (Get-ChildItem $_.FullName -File -Recurse).Count
        Write-Host ("  DIR  {0}/ ({1} files)" -f $_.Name, $n)
    }
    else {
        Write-Host ("  {0,12:N0}  {1}" -f $_.Length, $_.Name)
    }
}

# Confirm locales landed in the zip (not only the stage folder).
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path $zipPath))
try {
    $localeEntries = @(
        $zip.Entries |
            Where-Object {
                $name = $_.FullName.Replace('\', '/')
                $name -like 'locales/*.yaml' -or $name -like 'locales/*.yml'
            }
    )
    if ($localeEntries.Count -lt 1) {
        throw "Zip is missing locales/*.yaml next to the exe."
    }
    Write-Host "=== Zip locales ==="
    $localeEntries | ForEach-Object { Write-Host ("  {0}" -f $_.FullName) }
}
finally {
    $zip.Dispose()
}

if (-not $CreateGitHubRelease) {
    Write-Host "Done (publish+zip only). Pass -CreateGitHubRelease to tag and upload."
    exit 0
}

Write-Host "=== Git tag + GitHub release ==="
$existingTag = git tag -l $tag
if (-not $existingTag) {
    git tag -a $tag -m "Release $tag - Master Duel Modding Tool by AmidoriA"
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
    git push origin $tag
    if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }
}
else {
    Write-Host "Tag $tag already exists locally; not recreating (no force)."
    git push origin $tag 2>$null
}

$notes = @"
## Master Duel Modding Tool by AmidoriA

Release $tag.

### Download
- **$zipName** - self-contained Windows x64 (includes .NET runtime). Unzip and run ``$exeName``.

Ships with ``database.db``, ``classdata.tpk``, ``THIRD_PARTY_NOTICES.txt``, ``frames/``, and ``locales/``.

### Tutorial
https://youtu.be/jXaKaVDhXdg

### Notes
Portions of this project may include AI-assisted contributions; review and test before use in competitive or shared environments.
"@

$releaseExists = $false
gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    $releaseExists = $true
}

if ($releaseExists) {
    gh release upload $tag $zipPath --clobber
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed" }
}
else {
    gh release create $tag $zipPath --title "$tag - Master Duel Modding Tool by AmidoriA" --notes $notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed" }
}

gh release view $tag --json url,tagName,assets
Write-Host "Done."
