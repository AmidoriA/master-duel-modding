# Masterduel modding tool by AmidoriA

Windows desktop tool for replacing **Yu-Gi-Oh! Master Duel** card art and applying **over-frame** mods.

**This project’s code is 100% AI-generated.**

Repository: [AmidoriA/master-duel-modding](https://github.com/AmidoriA/master-duel-modding)

## What it is

- Browse / search cards from the shipped master catalog (`database.db`)
- Preview current Texture2D art from Master Duel AssetBundles
- Validate and prepare replacement images, back up originals, then write new art
- Apply over-frame mods (704×1024), including auto-create with local background removal
- OF compose automatically Real-ESRGAN-upscales 512×512 (and near-512) art to 1024×1024 before Cover — see [docs/of-art-upscale.md](docs/of-art-upscale.md)
- Custom OF Advanced → **Add more selection**: paint a region, then SAM 2 (ONNX) segments subjects into Card Art (union with existing subject) — see [docs/sam2-point-cutout.md](docs/sam2-point-cutout.md)
- Tools to refresh the catalog from a local install and restore over-frames after a game patch

## Tutorial

Watch the walkthrough: [https://youtu.be/jXaKaVDhXdg](https://youtu.be/jXaKaVDhXdg)

## Requirements

- Windows
- .NET 8 SDK
- A Master Duel install (for live replace / catalog refresh)
- Shipped `database.db` (writable `user.db` is created next to the exe on first run)
- `classdata.tpk` (shipped under `src/Floowan.Core/Resources`)

## Build & run (Windows)

```powershell
dotnet build Floowan.sln -c Release
dotnet test Floowan.sln -c Release
dotnet run --project src/Floowan.Desktop -c Release
```

Solution and project folders still use the historical `Floowan.*` names.

| Project | Role |
|---------|------|
| `src/Floowan.Core` | Non-UI logic (DB, paths, image prep, AssetBundle I/O) |
| `src/Floowan.Desktop` | WPF UI |
| `tests/Floowan.Core.Tests` | Unit tests |

## Localization (adding a language)

UI strings live in YAML under `src/Floowan.Desktop/locales/`. The app auto-loads every `*.yaml` / `*.yml` it finds in the `locales/` folder next to the exe (no hardcoded language list).

### File naming and format

- Name files as `xx-XX.yaml` (BCP 47 culture code), e.g. `en-US.yaml`, `th-TH.yaml`, `ja-JP.yaml`.
- Nested YAML keys become dotted lookup keys (`tabs.card_art`, `status.ready`, …).
- Dynamic messages use .NET format placeholders: `"Loaded. Cards in DB: {0}."`

### Add a new language

1. Copy `src/Floowan.Desktop/locales/en-US.yaml` to `xx-XX.yaml` (same folder). **en-US is the key source of truth** — keep the same keys; translate values only.
2. For shipping with the app/build, keep the file in `src/Floowan.Desktop/locales/` (the Desktop csproj copies it to output and publish).
3. For a quick local test against an already-built/unzipped release, drop `xx-XX.yaml` into `<exe-dir>/locales/` next to `Floowan.Desktop.exe`.
4. Open the **Options** tab → choose the language, or click **Reload locale files** after adding a file at runtime.
5. Missing keys in the active language **fall back to en-US**. Unknown cultures also resolve to en-US.

Release packaging (`scripts/publish-release.ps1` / the publish skill) stages `locales/` beside the single-file exe in the zip, same pattern as `frames/` and `database.db`.

## Credits & links

- Heavily inspired by [Floowandereeze and Modding](https://github.com/Nauder/floowandereeze-and-modding-qt) by Nauder (GPL-3.0). Path layout and card-art replacement approaches were pioneered there; this C# tool reimplements related behavior independently. Please give Floowandereeze the credit it deserves.
- Catalog ETL ideas also draw from [floowandereeze-and-modding-etl](https://github.com/Nauder/floowandereeze-and-modding-etl); the shipped `database.db` is maintained in this project.
- Over-frame steps follow the community [Nexus Mods over-frame guide](https://www.nexusmods.com/yugiohmasterduel/articles/103).
- Third-party licenses and attributions: [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt)

## Limitations

- Card text / names / descriptions are not edited.
- Replacements use **RGBA32** (larger than BC7).
- Game updates can overwrite LocalData assets — keep backups.
- Auto background removal is a starting point; complex art may need manual cleanup.
