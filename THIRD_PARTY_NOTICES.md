# Third-party notices

## AssetsTools.NET / UABEA

- https://github.com/nesrak1/AssetsTools.NET
- https://github.com/nesrak1/UABEA
- License: MIT
- Used for Unity AssetBundle / Texture2D reading and writing.
- `classdata.tpk` is redistributed from UABEA release files for Unity type trees.

## AssetStudio (reference only)

- https://github.com/Perfare/AssetStudio
- Used as a research reference for Unity asset layouts; not linked into this build.

## Floowandereeze and Modding (Qt)

- https://github.com/Nauder/floowandereeze-and-modding-qt
- License: GPL-3.0
- Reference for Master Duel path layout (`LocalData/<id>/0000/...`), card `database.db` schema,
  and the practical card-art replacement strategy (Texture2D → RGBA32, LZ4 pack, backups).
- This C# project reimplements those behaviors independently with AssetsTools.NET.
  It does not copy Floowandereeze Python source. If you distribute a combined work that
  incorporates Floowandereeze GPL code, comply with GPL-3.0 for that combined work.

## SixLabors.ImageSharp

- https://github.com/SixLabors/ImageSharp
- License: Apache-2.0
- Image load / resize / PNG export for preparation and previews.

## Microsoft.Data.Sqlite

- License: MIT
- Reads Floowandereeze-compatible `database.db`.

## rembg / IS-Net anime (`isnet-anime`)

- https://github.com/danielgatis/rembg
- https://github.com/SkyTNT/anime-segmentation
- Licenses: rembg MIT; anime-segmentation model per upstream project
- The automatic over-frame generator independently ports rembg's documented
  `isnet-anime` preprocessing/postprocessing to .NET. The ONNX model is downloaded
  from the official rembg release on first use and is not stored in this repository.

## Microsoft.ML.OnnxRuntime

- https://github.com/microsoft/onnxruntime
- License: MIT
- Runs the `isnet-anime` background-removal model locally.

## MattOstgard spine_sequence (reference)

- https://github.com/MattOstgard/spine_sequence
- Inspiration for a future image-sequence → Spine attachment timeline path.
- v1 cut-in animation uses an independent C# bone-translate bob generator instead.

## daominah monster_cutin coverage table

- https://github.com/daominah/yugioh_master_duel_card_art
- The embedded `monster_cutin.csv` lists Master Duel CardIDs that ship summon cut-ins.
- Used only as an eligibility/lookup table; game assets are not redistributed.
