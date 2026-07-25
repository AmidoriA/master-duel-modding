# Floowan

.NET 8 WPF desktop tool for replacing **Yu-Gi-Oh! Master Duel** card art and applying **over-frame** mods.

## Features (MVP)

- Discover / browse Master Duel `LocalData/<playerId>` paths (Steam libraries + registry)
- Load and search cards from Floowan’s master `database.db` (inspired by Floowandereeze catalog ideas; schema/contents maintained here — user settings/state in `user.db` beside the app)
- **Tools → Update entire DB** / **Update new files only**: rebuild or incrementally refresh master catalog from LocalData + `masterduel_Data/StreamingAssets/AssetBundle` (CARD_* decrypt + illust scan); stores `card_type` / `created_at`. Incremental compares illustration `File.GetCreationTimeUtc` against DB `MAX(created_at)`.
- **Tools → Restore overframes after patch**: re-register Floowan OF cards into the live `of_card_asset` gate (additive merge) and re-apply backed-up OF canvases after an MD update
- Preview current Texture2D art from the card AssetBundle
- Validate / prepare a replacement image (resize to texture size, RGBA32)
- Back up the original bundle file before writing
- Replace card art in the Unity AssetBundle via **AssetsTools.NET** (UABEA family)
- Restore from backup
- **Pendulum card art**: art aspect is **3:4** (canonical export **512×683**). Live MD Texture2D is often a taller **512×1024** canvas — extract **resizes** the full canvas to **512×683** (no crop). Import accepts **512×512**, **512×683**, **512×1024**, and other **3:4** images — **512×683** stretches back onto the tall canvas (round-trip); other aspect mismatches are letterboxed
- **Over-frame tab**: apply **704×1024** art, register the card in `of_card_asset`, enable/remove gate entries, restore backups
- **Pendulum over-frame**: Frame dropdown lists all MD Pendulum variants (Normal / Effect / Fusion / Synchro / Xyz / Token). Auto-create crops native 512×1024 sources to the top **3:4** band and composites into each style’s wider/shorter art hole (~603×450)
- **Auto-create over-frame art**: remove the current art background with rembg’s `isnet-anime` model, trim/resize the subject, and preview it on a transparent 704×1024 canvas
- **Link over-frame**: Link frames are supported for Auto-create and Custom OF; active `CARD_Prop` markers light only the inner triangle glyph (orange/red gradient) as a topmost overlay after compose — metallic bevel stays from `Link.png`, inactive arrows stay dark

## Projects

| Project | Role |
|---------|------|
| `src/Floowan.Core` | Non-UI logic: DB, paths, image prep, bundle read/write, over-frame gate |
| `src/Floowan.Desktop` | WPF UI (Card Art + Over-frame tabs) |
| `tests/Floowan.Core.Tests` | Unit + optional integration tests |

## Requirements

- Windows, .NET 8 SDK
- Master Duel install (for live replace)
- `database.db` master catalog (shipped in repo root; rebuild/refresh via **Tools** from a local Master Duel install). Inspired by [Floowandereeze ETL](https://github.com/Nauder/floowandereeze-and-modding-etl) / [Modding Qt](https://github.com/Nauder/floowandereeze-and-modding-qt), but **not** a verbatim upstream dump — Floowan adds fields such as `card_type` / `created_at` and its own update flow. Writable `user.db` is created next to the exe on first run
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
5. On first use of the Over-frame tab (or via **Scan / locate of_card_asset**), Floowan finds the bundle containing TextAsset `of_card_asset`, caches its id in `user.db` `app_config`, and can sync `is_overframe` flags from the gate.
6. **Apply over-frame** backs up the card bundle + gate bundle, saves the applied 704×1024 canvas as `backups/cards/{slug}-applied-overframe.png`, replaces the live texture, adds a LE ushort pair `(artId, baseArtId)` to the gate, and records `floowan_overframe` in `user.db`.
7. **Enable gate only** / **Remove over-frame** edit the gate without requiring a new image; **Restore backups** reverts card art and drops this card’s gate row (never restores the whole shared gate backup).
8. **Tools → Restore overframes after patch**: after an MD update replaces `of_card_asset`, re-applies Floowan OF from `user.db` (`floowan_overframe`) + applied PNG backups into the **current** gate via additive merge (official Konami OF entries stay).

Game updates may reset `of_card_asset` (and card bundles). Keep backups under `backups/bundles/cards`, `backups/bundles/gate`, and `backups/cards/*-applied-overframe.png`.

## Limitations

- **Card text / names / descriptions** are not edited (those use encrypted metadata + crypto key in Floowandereeze).
- **Sleeves, fields, icons, wallpapers** are out of scope for this MVP (architecture is ready to extend).
- Replacement forces **RGBA32** (larger than BC7). This matches Floowandereeze’s UnityPy `set_image(..., RGBA32)` approach and is the reliably writable path without native texture encoders.
- Automatic background removal is an AI-assisted starting point. Complex artwork may need manual cleanup before applying.
- Orphaned `.resS` directory entries may remain inside the bundle after inlining texture bytes; the Texture2D no longer references them.
- Always keep backups; game updates can overwrite LocalData assets.

## Attribution

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Path layout and art-replacement approach are inspired by Floowandereeze and Modding (GPL-3.0); Floowan’s `database.db` is maintained separately (Tools update, extra columns). Bundle I/O uses AssetsTools.NET / UABEA (MIT). Over-frame steps follow the community Nexus guide linked above.
