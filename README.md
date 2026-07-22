# Floowan

.NET 8 WPF desktop tool for replacing **Yu-Gi-Oh! Master Duel** card art and applying **over-frame** mods.

## Features (MVP)

- Discover / browse Master Duel `LocalData/<playerId>` paths (Steam libraries + registry)
- Load and search cards from Floowandereeze-compatible `database.db`
- Preview current Texture2D art from the card AssetBundle
- Validate / prepare a replacement image (resize to texture size, RGBA32)
- Back up the original bundle file before writing
- Replace card art in the Unity AssetBundle via **AssetsTools.NET** (UABEA family)
- Restore from backup
- **Over-frame tab**: apply **704×1024** art, register the card in `of_card_asset`, enable/remove gate entries, restore backups
- **Auto-create over-frame art**: remove the current art background with rembg’s `isnet-anime` model, trim/resize the subject, and preview it on a transparent 704×1024 canvas
- **Cut-in Animation tab**: for cards that already ship a summon cut-in, generate a simple Spine bob from one PNG (optional ONNX subject crop) and overwrite texture + atlas + skeleton TextAssets

## Projects

| Project | Role |
|---------|------|
| `src/Floowan.Core` | Non-UI logic: DB, paths, image prep, bundle read/write, over-frame gate |
| `src/Floowan.Desktop` | WPF UI (Card Art + Over-frame + Cut-in Animation tabs) |
| `tests/Floowan.Core.Tests` | Unit + optional integration tests |

## Requirements

- Windows, .NET 8 SDK
- Master Duel install (for live replace)
- `database.db` (already in repo root, or from [Floowandereeze and Modding](https://github.com/Nauder/floowandereeze-and-modding-qt))
- `classdata.tpk` (shipped under `src/Floowan.Core/Resources`, from UABEA release files)

## Build & run

```powershell
dotnet build Floowan.sln -c Release
dotnet test Floowan.sln -c Release
dotnet run --project src/Floowan.Desktop -c Release
```

## How replacement works

1. Resolve bundle: `{LocalData}/{playerId}/0000/{bundle[0..2]}/{bundle}`
2. Load AssetBundle with AssetsTools.NET
3. Find first `Texture2D` (Master Duel card art is typically BC7 + `.resS`)
4. Encode replacement as **RGBA32**, clear `m_StreamData`, write asset + CAB directory entry
5. Write uncompressed bundle, then **LZ4** pack (same packer Floowandereeze defaults to)
6. Overwrite the live bundle path

## Over-frame workflow

Automates the [Nexus Mods over-frame guide](https://www.nexusmods.com/yugiohmasterduel/articles/103):

1. Prefer replacement art at exactly **704×1024**. Other sizes are stretched with a warning.
2. Or select a card and click **Auto-create from current art**. Floowan extracts the current texture, removes its background, composites it under a **card frame with a transparent art hole** (from Master Duel `card_frame*` faces), and places an opaque cutout overflow on top. Pick the frame style (Normal / Effect / Fusion / …) in the Over-frame tab; it is auto-suggested from card text when possible. The first run downloads and verifies the rembg `isnet-anime` ONNX model (~168 MB) under `%LOCALAPPDATA%\Floowan\models`; Python and the rembg CLI are not required.
3. Keep **RGBA32** (not BC7) — same writable path as normal card-art replace.
4. Tip: main art / frame-overlap regions must use alpha ≈ **4** (not 0). Official over-frames and the Nexus guide comments use this as the foil/coverage mask; alpha 0 blacks out the card frame.
5. On first use of the Over-frame tab (or via **Scan / locate of_card_asset**), Floowan finds the bundle containing TextAsset `of_card_asset`, caches its id in `app_config`, and can sync `is_overframe` flags from the gate.
6. **Apply over-frame** backs up the card bundle + gate bundle, replaces texture at 704×1024, and adds a LE ushort pair `(cardId, cardId)` to the gate.
7. **Enable gate only** / **Remove over-frame** edit the gate without requiring a new image; **Restore backups** reverts card and/or gate files.

Game updates may reset `of_card_asset` (and card bundles). Keep backups under `backups/bundles/cards` and `backups/bundles/gate`.

## Cut-in animation workflow

v1 only works for cards that **already** have a Master Duel summon cut-in (`P####` texture + atlas + skeleton):

1. Open the **Cut-in Animation** tab and pick a card marked `[cut-in]` (catalog from known monstercutin IDs).
2. Select a source PNG. Optionally keep **Crop subject (ONNX)** enabled to reuse the same `isnet-anime` cutout as over-frame.
3. Floowan builds a minimal Spine 4.0 package with a gentle bone translate bob (no AssetStudio / UABEA / Spine editor / Python).
4. **Apply simple animation** backs up touched cut-in bundles, then replaces the Texture2D + atlas + JSON TextAssets and LZ4-repacks.
5. Restart Master Duel to preview. **Restore backup** reverts the cut-in bundles for that `P####` id.

Optional **Build cut-in index** scans LocalData once and caches bundle locations for faster applies.

## Limitations

- **Card text / names / descriptions** are not edited (those use encrypted metadata + crypto key in Floowandereeze).
- **Sleeves, fields, icons, wallpapers** are out of scope for this MVP (architecture is ready to extend).
- **Brand-new cut-ins** for cards that never shipped one are out of scope for v1 (replace-existing only).
- Replacement forces **RGBA32** (larger than BC7). This matches Floowandereeze’s UnityPy `set_image(..., RGBA32)` approach and is the reliably writable path without native texture encoders.
- Automatic background removal is an AI-assisted starting point. Complex artwork may need manual cleanup before applying.
- Orphaned `.resS` directory entries may remain inside the bundle after inlining texture bytes; the Texture2D no longer references them.
- Always keep backups; game updates can overwrite LocalData assets.

## Attribution

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Approach and `database.db` schema align with Floowandereeze and Modding (GPL-3.0). Bundle I/O uses AssetsTools.NET / UABEA (MIT). Over-frame steps follow the community Nexus guide linked above.
