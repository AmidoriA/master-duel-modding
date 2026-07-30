---
name: build-and-publish-release
description: >-
  Builds and publishes Masterduel modding tool by AmidoriA Windows releases
  (single-file self-contained win-x64 zip, annotated git tag, GitHub release).
  Use when the user asks to release, publish, tag, cut a version, ship a
  single-file exe, upload a GitHub release, or rebuild MasterDuelModding-vX.Y.Z-win-x64.zip.
---

# Build and publish release

Product: **Masterduel modding tool by AmidoriA**  
Repo: `AmidoriA/master-duel-modding`  
Desktop: `src/Floowan.Desktop/Floowan.Desktop.csproj` (Windows-only WPF, `net8.0-windows`)

Proven pattern: **v0.0.1** (PublishSingleFile + sidecars; no loose DLLs required).

## Checklist

Copy and track:

```
Release Progress:
- [ ] 1. Confirm version (csproj + About UI)
- [ ] 2. Publish single-file win-x64
- [ ] 3. Stage zip sidecars (exclude pdb/lib)
- [ ] 4. Verify zip contents
- [ ] 5. Annotated tag vX.Y.Z (only if releasing)
- [ ] 6. gh release create / upload --clobber
- [ ] 7. Return release URL + artifact name
```

Do **not** force-push tags or history unless the user explicitly asks.

## 1. Confirm version

Before tagging, version `X.Y.Z` must match:

- `src/Floowan.Desktop/Floowan.Desktop.csproj`: `<Version>`, `<InformationalVersion>`, and related assembly versions
- About UI string in Desktop (e.g. `Version X.Y.Z` in `MainWindow.xaml`)

Tag form: `vX.Y.Z` (example: `0.0.1` → `v0.0.1`).

Prefer building from a clean tip of `master` (or the commit the user names) after `git fetch`.

## 2. Publish (single-file + sidecars)

Windows only. From repo root:

```powershell
dotnet publish src/Floowan.Desktop/Floowan.Desktop.csproj `
  -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o artifacts/publish/win-x64-sf
```

Or run:

```powershell
./scripts/publish-release.ps1 -Version X.Y.Z
```

Script does publish + zip only (no tag/upload unless `-CreateGitHubRelease` is passed).

### Expected publish output

Beside the single exe (names may change later; currently `Floowan.Desktop.exe`):

| Required in zip | Notes |
|-----------------|--------|
| `Floowan.Desktop.exe` | Single-file self-contained |
| `database.db` | Sidecar (copied by csproj) |
| `classdata.tpk` | Sidecar |
| `THIRD_PARTY_NOTICES.txt` | Sidecar |
| `frames/` | PNG templates (from Core) |
| `locales/` | UI translation YAML (`en-US.yaml`, `th-TH.yaml`, …) |

- `user.db` is **runtime-created** — do not require in zip
- No loose `.dll` beside exe if `IncludeNativeLibrariesForSelfExtract` works (ONNX natives extract at runtime)
- Publish dir may also contain `.pdb` / `.lib` — **do not** put those in the release zip

If single-file breaks native/ONNX, fall back to single-file managed + leave required native DLLs beside the exe (document what was left loose).

## 3. Stage and zip

Artifact name: `MasterDuelModding-vX.Y.Z-win-x64.zip` under `artifacts/`.

Stage only the required files (do not zip the whole publish folder):

```powershell
$ver = "X.Y.Z"
$src = "artifacts/publish/win-x64-sf"
$stage = "artifacts/publish/win-x64-sf-stage"
$zip = "artifacts/MasterDuelModding-v$ver-win-x64.zip"

Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null
Copy-Item "$src/Floowan.Desktop.exe" $stage
Copy-Item "$src/database.db" $stage
Copy-Item "$src/THIRD_PARTY_NOTICES.txt" $stage
Copy-Item "$src/classdata.tpk" $stage
Copy-Item "$src/frames" "$stage/frames" -Recurse
Copy-Item "$src/locales" "$stage/locales" -Recurse

Remove-Item $zip -Force -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage/*" -DestinationPath $zip
```

Do not commit `artifacts/` output.

## 4. Verify zip

Confirm the zip contains exactly:

- One `.exe` at the root
- `database.db`, `classdata.tpk`, `THIRD_PARTY_NOTICES.txt`
- `frames/*.png` (dozens of frame PNGs)
- `locales/*.yaml` (at least `en-US.yaml`; typically also `th-TH.yaml`)
- No `user.db`, no `.pdb`, no `.lib`, no required loose `.dll` (unless native fallback)

## 5. Git tag (release only)

Annotated tag matching Desktop version:

```powershell
git tag -a vX.Y.Z -m "Release vX.Y.Z — Master Duel Modding Tool by AmidoriA"
git push origin vX.Y.Z
```

Never `git push --force` a tag unless the user explicitly requests it.

## 6. GitHub release

Create:

```powershell
gh release create vX.Y.Z "artifacts/MasterDuelModding-vX.Y.Z-win-x64.zip" `
  --title "vX.Y.Z — Master Duel Modding Tool by AmidoriA" `
  --notes @"
## Master Duel Modding Tool by AmidoriA

Release vX.Y.Z.

### Download
- **MasterDuelModding-vX.Y.Z-win-x64.zip** — self-contained Windows x64 (includes .NET runtime). Unzip and run ``Floowan.Desktop.exe``.

Ships with ``database.db``, ``classdata.tpk``, ``THIRD_PARTY_NOTICES.txt``, ``frames/``, and ``locales/``.

### Tutorial
https://youtu.be/jXaKaVDhXdg

### Notes
Portions of this project may include AI-assisted contributions; review and test before use in competitive or shared environments.
"@
```

Tutorial link is optional in notes; include unless the user asks to omit it.

Replace an existing asset (same tag):

```powershell
gh release upload vX.Y.Z "artifacts/MasterDuelModding-vX.Y.Z-win-x64.zip" --clobber
```

## 7. Done

Return:

- Release URL (`gh release view vX.Y.Z --json url -q .url`)
- Artifact name and brief zip contents
- Whether the tag was new or reused; confirm no force-push

## Script

`scripts/publish-release.ps1`:

| Flag | Behavior |
|------|----------|
| `-Version X.Y.Z` | Required. Publish + stage + zip |
| `-CreateGitHubRelease` | Also annotated tag + `gh release create` (or upload `--clobber` if release exists) |
| `-SkipPublish` | Re-zip from existing `artifacts/publish/win-x64-sf` |

Default: build/zip only — never tag or upload without the flag (or an explicit user request).
