# Masterduel modding tool by AmidoriA

Windows desktop tool for replacing **Yu-Gi-Oh! Master Duel** card art and applying **over-frame** mods.

**This project’s code is 100% AI-generated.**

Repository: [AmidoriA/master-duel-modding](https://github.com/AmidoriA/master-duel-modding)

## What it is

- Browse / search cards from the shipped master catalog (`database.db`)
- Preview current Texture2D art from Master Duel AssetBundles
- Validate and prepare replacement images, back up originals, then write new art
- Apply over-frame mods (704×1024), including auto-create with local background removal
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
